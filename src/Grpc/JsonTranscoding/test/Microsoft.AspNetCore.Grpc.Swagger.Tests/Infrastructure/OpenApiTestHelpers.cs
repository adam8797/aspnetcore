// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using Google.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Grpc.Swagger;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using Swashbuckle.AspNetCore.Swagger;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.Grpc.Swagger.Tests.Infrastructure;

internal static class OpenApiTestHelpers
{
    public static OpenApiDocument GetOpenApiDocument(ITestOutputHelper testOutputHelper, params Type[] typeServices)
        => GetOpenApiDocument(testOutputHelper, configureOptions: null, typeServices);

    public static OpenApiDocument GetOpenApiDocument(ITestOutputHelper testOutputHelper, Action<GrpcSwaggerOptions>? configureOptions, params Type[] typeServices)
    {
        var services = new ServiceCollection();
        services.AddGrpcSwagger(configureOptions);
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });

            var filePath = Path.Combine(System.AppContext.BaseDirectory, "Microsoft.AspNetCore.Grpc.Swagger.Tests.xml");
            c.IncludeXmlComments(filePath);
            c.IncludeGrpcXmlComments(filePath, includeControllerXmlComments: true);
        });
        services.AddRouting();
        services.AddLogging();
        services.AddSingleton<IWebHostEnvironment, TestWebHostEnvironment>();
        var serviceProvider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(serviceProvider);

        app.UseRouting();
        app.UseEndpoints(c =>
        {
            foreach (var s in typeServices)
            {
                MapGrpcService(c, s);
            }
        });

        var swaggerGenerator = serviceProvider.GetRequiredService<ISwaggerProvider>();
        var swagger = swaggerGenerator.GetSwagger("v1");

        using var outputString = new StringWriter();
        swagger.SerializeAsV3(new OpenApiJsonWriter(outputString));
        testOutputHelper.WriteLine(outputString.ToString());

        return swagger;
    }

    public static OpenApiDocument GetOpenApiDocument<TService>(ITestOutputHelper testOutputHelper) where TService : class
    {
        return GetOpenApiDocument(testOutputHelper, typeof(TService));
    }

    private static void MapGrpcService(IEndpointRouteBuilder routes, Type grpcService)
    {
        var mapMethod = typeof(GrpcEndpointRouteBuilderExtensions).GetMethod(nameof(GrpcEndpointRouteBuilderExtensions.MapGrpcService))!;
        mapMethod.MakeGenericMethod(grpcService).Invoke(null, [routes]);
    }
}
