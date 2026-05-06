using Asp.Versioning;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Api.Management.OpenApi;
using Umbraco.Cms.Api.Common.OpenApi;

namespace Umbraco.Community.AiVisibility.Composing
{
    public class BackofficeApiComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {

            builder.Services.AddSingleton<IOperationIdHandler, CustomOperationHandler>();

            builder.Services.Configure<SwaggerGenOptions>(opt =>
            {
                // Related documentation:
                // https://docs.umbraco.com/umbraco-cms/tutorials/creating-a-backoffice-api
                // https://docs.umbraco.com/umbraco-cms/tutorials/creating-a-backoffice-api/adding-a-custom-swagger-document
                // https://docs.umbraco.com/umbraco-cms/tutorials/creating-a-backoffice-api/versioning-your-api
                // https://docs.umbraco.com/umbraco-cms/tutorials/creating-a-backoffice-api/access-policies

                // Configure the Swagger generation options
                // Add in a new Swagger API document solely for our own package that can be browsed via Swagger UI
                // Along with having a generated swagger JSON file that we can use to auto generate a TypeScript client
                opt.SwaggerDoc(Constants.ApiName, new OpenApiInfo
                {
                    Title = "Umbraco Community AI Visibility Backoffice API",
                    Version = "1.0",
                    // Contact = new OpenApiContact
                    // {
                    //     Name = "Some Developer",
                    //     Email = "you@company.com",
                    //     Url = new Uri("https://company.com")
                    // }
                });

                // Enable Umbraco authentication for the "Example" Swagger document
                // PR: https://github.com/umbraco/Umbraco-CMS/pull/15699
                opt.OperationFilter<BackofficeOperationSecurityFilter>();
            });
        }

        public class BackofficeOperationSecurityFilter : BackOfficeSecurityRequirementsOperationFilterBase
        {
            protected override string ApiName => Constants.ApiName;
        }

        // Story 6.0a (Codex finding #2) — explicit allow-list of intended
        // Management API controllers. The previous namespace-prefix match
        // (`Umbraco.Community.AiVisibility.Controllers`) silently captured every controller
        // under that namespace, including the deleted spike + template
        // scaffolds, and would silently produce duplicate operation IDs in
        // Swagger if two future actions named identically appeared across
        // controllers. New controllers MUST be added to AllowedControllers
        // explicitly — do NOT replace this with reflection-based discovery
        // of all `ManagementApiControllerBase` subclasses; that re-introduces
        // the original "everything in this namespace" footgun.
        // https://docs.umbraco.com/umbraco-cms/tutorials/creating-a-backoffice-api/umbraco-schema-and-operation-ids#operation-ids
        public class CustomOperationHandler : OperationIdHandler
        {
            private static readonly Type[] AllowedControllers = new[]
            {
                typeof(global::Umbraco.Community.AiVisibility.Backoffice.SettingsManagementApiController),
                typeof(global::Umbraco.Community.AiVisibility.Backoffice.AnalyticsManagementApiController),
            };

            public CustomOperationHandler(IOptions<ApiVersioningOptions> apiVersioningOptions) : base(apiVersioningOptions)
            {
            }

            protected override bool CanHandle(ApiDescription apiDescription, ControllerActionDescriptor controllerActionDescriptor)
            {
                return Array.IndexOf(AllowedControllers, controllerActionDescriptor.ControllerTypeInfo.AsType()) >= 0;
            }

            public override string Handle(ApiDescription apiDescription) => $"{apiDescription.ActionDescriptor.RouteValues["action"]}";
        }
    }
}
