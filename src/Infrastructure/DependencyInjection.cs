﻿using Cfo.Cats.Application.Common.Interfaces;
using Cfo.Cats.Application.Common.Interfaces.Identity;
using Cfo.Cats.Application.Common.Interfaces.MultiTenant;
using Cfo.Cats.Application.Common.Interfaces.Serialization;
using Cfo.Cats.Domain.Identity;
using Cfo.Cats.Infrastructure.Configurations;
using Cfo.Cats.Infrastructure.Constants.ClaimTypes;
using Cfo.Cats.Infrastructure.Constants.Database;
using Cfo.Cats.Infrastructure.PermissionSet;
using Cfo.Cats.Infrastructure.Persistence;
using Cfo.Cats.Infrastructure.Persistence.Interceptors;
using Cfo.Cats.Infrastructure.Services;
using Cfo.Cats.Infrastructure.Services.Identity;
using Cfo.Cats.Infrastructure.Services.MultiTenant;
using Cfo.Cats.Infrastructure.Services.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Cfo.Cats.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddSettings(configuration)
            .AddDatabase(configuration)
            .AddServices(configuration);

        services.AddAuthenticationService(configuration).AddFusionCacheService();

        services.AddSingleton<IUsersStateContainer, UsersStateContainer>();

        return services;
    }

    private static IServiceCollection AddSettings(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .Configure<IdentitySettings>(configuration.GetSection(IdentitySettings.Key))
            .AddSingleton(s => s.GetRequiredService<IOptions<IdentitySettings>>().Value)
            .AddSingleton<IIdentitySettings>(s =>
                s.GetRequiredService<IOptions<IdentitySettings>>().Value
            );

        services
            .Configure<DatabaseSettings>(configuration.GetSection(DatabaseSettings.Key))
            .AddSingleton(s => s.GetRequiredService<IOptions<DatabaseSettings>>().Value);

        return services;
    }

    private static IServiceCollection AddDatabase(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddScoped<ISaveChangesInterceptor, AuditableEntityInterceptor>()
            .AddScoped<ISaveChangesInterceptor, DispatchDomainEventsInterceptor>();

        if (configuration.GetValue<bool>("UseInMemoryDatabase"))
        {
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseInMemoryDatabase("CatsDb");
                options.EnableSensitiveDataLogging();
            });
        }
        else
        {
            services.AddDbContext<ApplicationDbContext>(
                (p, m) =>
                {
                    var databaseSettings = p.GetRequiredService<IOptions<DatabaseSettings>>().Value;
                    m.AddInterceptors(p.GetServices<ISaveChangesInterceptor>());
                    m.UseDatabase(databaseSettings.DbProvider, databaseSettings.ConnectionString);
                }
            );
        }

        services.AddScoped<
            IDbContextFactory<ApplicationDbContext>,
            BlazorContextFactory<ApplicationDbContext>
        >();
        services.AddTransient<IApplicationDbContext>(provider =>
            provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext()
        );
        services.AddScoped<ApplicationDbContextInitializer>();

        return services;
    }

    private static DbContextOptionsBuilder UseDatabase(
        this DbContextOptionsBuilder builder,
        string dbProvider,
        string connectionString
    )
    {
        switch (dbProvider.ToLowerInvariant())
        {
            case DbProviderKeys.SqlServer:
                return builder.UseSqlServer(
                    connectionString,
                    e => e.MigrationsAssembly("Cfo.Cats.Migrators.MSSQL")
                );

            case DbProviderKeys.SqLite:
                return builder.UseSqlite(
                    connectionString,
                    e => e.MigrationsAssembly("Cfo.Cats.Migrators.SqLite")
                ).EnableSensitiveDataLogging();

            default:
                throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.");
        }
    }

    private static IServiceCollection AddServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<PicklistService>()
            .AddSingleton<IPicklistService>(sp =>
            {
                var service = sp.GetRequiredService<PicklistService>();
                service.Initialize();
                return service;
            });
        
        services
            .AddSingleton<TenantService>()
            .AddSingleton<ITenantService>(sp =>
            {
                var service = sp.GetRequiredService<TenantService>();
                service.Initialize();
                return service;
            });
        
        services.Configure<NotifyOptions>(configuration.GetSection(NotifyOptions.Notify));

        return services
            .AddSingleton<ISerializer, SystemTextJsonSerializer>()
            .AddScoped<ICurrentUserService, CurrentUserService>()
            .AddScoped<ITenantProvider, TenantProvider>()
            .AddScoped<IValidationService, ValidationService>()
            .AddScoped<IDateTime, DateTimeService>()
            .AddScoped<ICommunicationsService, CommunicationsService>()
            .AddScoped<IExcelService, ExcelService>()
            .AddScoped<IUploadService, UploadService>();
    }

    private static IServiceCollection AddAuthenticationService(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        
        services.Configure<AllowlistOptions>(configuration.GetSection(nameof(AllowlistOptions)));
        
        services
            .AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            //.AddSignInManager()
            .AddClaimsPrincipalFactory<ApplicationUserClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();
        
       services.AddScoped<SignInManager<ApplicationUser>, CustomSigninManager<ApplicationUser>>();
       services.AddScoped<ISecurityStampValidator, SecurityStampValidator<ApplicationUser>>();

        
        services.Configure<IdentityOptions>(options =>
        {
            var identitySettings = configuration
                .GetRequiredSection(IdentitySettings.Key)
                .Get<IdentitySettings>();

            // Password settings
            options.Password.RequireDigit = identitySettings!.RequireDigit;
            options.Password.RequiredLength = identitySettings.RequiredLength;
            options.Password.RequireNonAlphanumeric = identitySettings.RequireNonAlphanumeric;
            options.Password.RequireUppercase = identitySettings.RequireUpperCase;
            options.Password.RequireLowercase = identitySettings.RequireLowerCase;

            // Lockout settings
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(
                identitySettings.DefaultLockoutTimeSpan
            );
            options.Lockout.MaxFailedAccessAttempts = 10;
            options.Lockout.AllowedForNewUsers = true;

            // Default SignIn settings.
            options.SignIn.RequireConfirmedEmail = true;
            options.SignIn.RequireConfirmedPhoneNumber = false;
            options.SignIn.RequireConfirmedAccount = true;

            // User settings
            options.User.RequireUniqueEmail = true;
            //options.Tokens.EmailConfirmationTokenProvider = "Email";
        });

        services
            .AddScoped<IIdentityService, IdentityService>()
            .AddAuthorizationCore(options =>
            {
                var permissions = Permissions.GetRegisteredPermissions();

                foreach (var propertyValue in permissions)
                {
                    options.AddPolicy(
                        propertyValue,
                        policy =>
                            policy.RequireClaim(ApplicationClaimTypes.Permission, propertyValue)
                    );
                }
            })
            .AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies(options => { });

        services.AddDataProtection().PersistKeysToDbContext<ApplicationDbContext>();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/pages/authentication/login";
        });
        services
            .AddSingleton<UserService>()
            .AddSingleton<IUserService>(sp =>
            {
                var service = sp.GetRequiredService<UserService>();
                service.Initialize();
                return service;
            });

        return services;
    }

    private static IServiceCollection AddFusionCacheService(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services
            .AddFusionCache()
            .WithDefaultEntryOptions(
                new FusionCacheEntryOptions
                {
                    // CACHE DURATION
                    Duration = TimeSpan.FromMinutes(120),
                    // FAIL-SAFE OPTIONS
                    IsFailSafeEnabled = true,
                    FailSafeMaxDuration = TimeSpan.FromHours(8),
                    FailSafeThrottleDuration = TimeSpan.FromSeconds(30),
                    // FACTORY TIMEOUTS
                    FactorySoftTimeout = TimeSpan.FromMilliseconds(100),
                    FactoryHardTimeout = TimeSpan.FromMilliseconds(1500)
                }
            );
        return services;
    }
}
