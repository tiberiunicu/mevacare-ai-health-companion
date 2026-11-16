using System.Reflection;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Infrastructure.Auditing;
using MevaCare.AI.HealthCompanion.Infrastructure.Email;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string ConnectionStringName = "Default";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, IEnumerable<Assembly> moduleModelAssemblies)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured. See README.md (Configuration).");
        }

        services.AddOptions<PersistenceModelOptions>()
            .Configure(o => o.ModelAssemblies.AddRange(moduleModelAssemblies));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditableEntitiesInterceptor>();

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "core");
                sql.EnableRetryOnFailure(maxRetryCount: 3);
            });
            options.AddInterceptors(sp.GetRequiredService<AuditableEntitiesInterceptor>());
        });

        services.AddScoped<IAuditLog, AuditLog>();

        // E-mail: a queue the application writes to and a background service that delivers (SMTP when Email:Host is set, otherwise a logger).
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<EmailQueue>();
        services.AddSingleton<IEmailQueue>(sp => sp.GetRequiredService<EmailQueue>());
        services.AddSingleton<IEmailTransport>(sp => sp.GetRequiredService<IOptions<EmailOptions>>().Value.Enabled
            ? ActivatorUtilities.CreateInstance<SmtpEmailTransport>(sp)
            : ActivatorUtilities.CreateInstance<LoggingEmailTransport>(sp));
        services.AddHostedService<EmailDispatcher>();
        return services;
    }
}
