using System;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Security.Cryptography;
using AspNet.Security.OpenIdConnect.Primitives;
using Cierge.Data;
using Cierge.Filters;
using Cierge.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using OpenIddict;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore;

namespace Cierge
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IHostingEnvironment env)
        {
            Configuration = configuration;
            Env = env;
            SigningKey = new RsaSecurityKey(GetRsaSigningKey());
        }

        public IConfiguration Configuration { get; }
        public IHostingEnvironment Env { get; }
        public SecurityKey SigningKey { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            // Disabling HTTPS requirement is default since it is assumed that
            // this is running behind a reverse proxy that requires HTTPS
            var requireHttps = !String.IsNullOrWhiteSpace(Configuration["RequireHttps"]) && Boolean.Parse(Configuration["RequireHttps"]) == true;

            // Might need to manually set issuer if running behind reverse proxy or using JWTs
            var issuer = Configuration["Cierge:Issuer"];

            if (requireHttps)
                services.Configure<MvcOptions>(options =>
                {
                    options.Filters.Add(new RequireHttpsAttribute());
                });

            services.AddMvc();

            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseInMemoryDatabase("ApplicationDbContext");

                //   options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"));

                options.UseOpenIddict();
            });

            services.AddIdentity<ApplicationUser, IdentityRole>(options =>
                {
                    options.Lockout.AllowedForNewUsers = true;
                    options.User.RequireUniqueEmail = false;
                })
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            if (!String.IsNullOrWhiteSpace(Configuration["ExternalAuth:Google:ClientId"]))
            {
                services.AddAuthentication().AddGoogle(googleOptions =>
                {
                    googleOptions.ClientId = Configuration["ExternalAuth:Google:ClientId"];
                    googleOptions.ClientSecret = Configuration["ExternalAuth:Google:ClientSecret"];
                });
            }

            services.Configure<IdentityOptions>(options =>
            {
                options.ClaimsIdentity.UserNameClaimType = OpenIdConnectConstants.Claims.Name;
                options.ClaimsIdentity.UserIdClaimType = OpenIdConnectConstants.Claims.Subject;
                options.ClaimsIdentity.RoleClaimType = OpenIdConnectConstants.Claims.Role;
            });

            // Register the OpenIddict services.
            services.AddOpenIddict()
                .AddCore(options =>
                {
                    options.UseEntityFrameworkCore()
                        .UseDbContext<ApplicationDbContext>();
                });

            services.AddOpenIddict()
                .AddCore(options =>
                {
                    options.UseEntityFrameworkCore().UseDbContext<ApplicationDbContext>();
                })
                .AddServer(options =>
                {
                    options.UseMvc();

                    options.UseJsonWebTokens();

                    options.EnableAuthorizationEndpoint("/connect/authorize")
                        .EnableLogoutEndpoint("/connect/logout")
                        .EnableTokenEndpoint("/connect/token")
                        .EnableUserinfoEndpoint("/api/userinfo")
                        .EnableIntrospectionEndpoint("/api/introspect");

                    options.AllowAuthorizationCodeFlow()
                        .AllowPasswordFlow()
                        .AllowRefreshTokenFlow();

                    options.RegisterScopes(
                        //OpenIdConnectConstants.Scopes.OpenId,
                        OpenIdConnectConstants.Scopes.Email,
                        OpenIdConnectConstants.Scopes.Profile,
                        //OpenIdConnectConstants.Scopes.OfflineAccess,
                        OpenIddictConnectConstants.Scopes.Roles
                    );

                    if (!String.IsNullOrWhiteSpace(issuer))
                        options.SetIssuer(new Uri(issuer));

                    if (!requireHttps)
                        options.DisableHttpsRequirement();

                    if (Env.IsDevelopment())
                        options.AddEphemeralSigningKey();
                    else
                        options.AddSigningKey(SigningKey);

                    options.EnableRequestCaching();

                });

            services.AddCors(options =>
            {
                options.AddPolicy("AllowSpecificOrigin", builder => builder.WithOrigins("http://localhost:8000")
                    .AllowAnyMethod()
                    .AllowAnyHeader());
            });

            if (Env.IsDevelopment())
            {
                services.AddTransient<IEmailSender, DevMessageSender>();
                services.AddTransient<ISmsSender, DevMessageSender>();
            }
            else
            {
                services.AddScoped<IEmailSender, SmtpMessageSender>();
            }

            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            services.AddScoped<ValidateRecaptchaAttribute>();

            services.AddScoped<NoticeService>();

            services.AddScoped<EventsService>();

            // Allow JWT bearer authentication (for API calls)
            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
            JwtSecurityTokenHandler.DefaultOutboundClaimTypeMap.Clear();
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                    ValidateIssuer = false, // TODO: make configurable
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = SigningKey
                    };

                    options.Audience = Configuration["Cierge:Audience"];

                    if (!String.IsNullOrWhiteSpace(issuer))
                        options.Authority = issuer;

                    if (Env.IsDevelopment())
                        options.RequireHttpsMetadata = false;
                });

            // Allow cross-site cookies 
            services.ConfigureApplicationCookie(options =>
            {
                options.Cookie.SameSite = SameSiteMode.None;
            });
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseBrowserLink();
                app.UseDatabaseErrorPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
            }

            app.UseCors("AllowSpecificOrigin");

            app.UseStaticFiles();

            app.UseAuthentication();

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");
            });
        }

        public RSAParameters GetRsaSigningKey()
        {
            var path = Configuration["Cierge:RsaSigningKeyJsonPath"];
            RSAParameters parameters;

            if (!String.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                // Get RSA JSON from file
                string jsonString;
                FileStream fileStream = new FileStream(path, FileMode.Open);
                using(StreamReader reader = new StreamReader(fileStream))
                {
                    jsonString = reader.ReadToEnd();
                }

                dynamic paramsJson = JsonConvert.DeserializeObject(jsonString);

                parameters = new RSAParameters
                {
                    Modulus = paramsJson.Modulus != null ? Convert.FromBase64String(paramsJson.Modulus.ToString()) : null,
                    Exponent = paramsJson.Exponent != null ? Convert.FromBase64String(paramsJson.Exponent.ToString()) : null,
                    P = paramsJson.P != null ? Convert.FromBase64String(paramsJson.P.ToString()) : null,
                    Q = paramsJson.Q != null ? Convert.FromBase64String(paramsJson.Q.ToString()) : null,
                    DP = paramsJson.DP != null ? Convert.FromBase64String(paramsJson.DP.ToString()) : null,
                    DQ = paramsJson.DQ != null ? Convert.FromBase64String(paramsJson.DQ.ToString()) : null,
                    InverseQ = paramsJson.InverseQ != null ? Convert.FromBase64String(paramsJson.InverseQ.ToString()) : null,
                    D = paramsJson.D != null ? Convert.FromBase64String(paramsJson.D.ToString()) : null
                };

            }
            else
            {
                // Generate RSA key
                RSACryptoServiceProvider RSA = new RSACryptoServiceProvider(2048);

                parameters = RSA.ExportParameters(true);
            }

            return parameters;
        }
    }
}