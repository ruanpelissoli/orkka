namespace Orkka.Modules.Documents;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Documents.Infrastructure.EventHandlers;
using Orkka.Modules.Documents.Infrastructure.Services;
using Orkka.Modules.Documents.PublicApi;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Persistence;

public static class DocumentsModuleExtensions
{
    /// <summary>DbContext + migration registration only — used by the Migrator.</summary>
    public static IServiceCollection AddDocumentsPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<DocumentsDbContext>(configuration, DocumentsDbContext.Module);
        return services;
    }

    /// <summary>Full module: persistence, public contracts, PDF export, and event handlers.
    /// Used by both hosts.</summary>
    public static IServiceCollection AddDocumentsModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        // QuestPDF Community license: free while Orkka's annual gross revenue is < $1M USD.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        services.AddDocumentsPersistence(configuration);

        services.AddScoped<IDocumentsApi, DocumentsApi>();
        services.AddSingleton<PdfExportService>();
        services.AddSingleton<DocxExportService>();

        services.AddIntegrationEventHandler<ProjectDeleted, DocumentCleanupOnProjectDeleted>();

        return services;
    }
}
