// See https://aka.ms/new-console-template for more information

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using Microsoft.SemanticKernel;
using Nectar.Nimbus.DbModelCodeFirst.Models;
using SKNimbusDb.Configuration;
using SKNimbusDb.Extensions;
using SKNimbusDb.Plugins;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors((options) =>
{
    options.AddDefaultPolicy((policy) =>
    {
        policy
            .WithOrigins("https://chat.openai.com", "https://localhost:7012")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
var kernelSettings = KernelSettings.LoadSettings();
builder.Services
    .AddOptions<KernelSettings>()
    .Bind(builder.Configuration.GetSection("SemanticKernel"));

builder.Services.AddDbContext<NimbusDBContext>((options) =>
{
    options.UseSqlServer("Data Source=192.168.0.8;Initial Catalog=NimbusDBProducaoGV;User Id=NimbusBaseUser;Pwd=Senha");
});

builder.Services.AddSemanticKernel();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen((options) =>
{
    options.SwaggerDoc("v1", new OpenApiInfo()
    {
        Title = "NimbusOpenApi",
        Version = "v1",
        Description = "This is a sample API for Nimbus",
    });
});

var app = builder.Build();

app.UseCors();

app.UseSwagger((options) =>
{
    options.PreSerializeFilters.Add((swaggerDocument, httpRequest) =>
    {
        swaggerDocument.Servers = new List<OpenApiServer>()
        {
            new()
            {
                Url = $"{httpRequest.Scheme}://{httpRequest.Host.Value}"
            }
        };
    });
});

app.UseSwaggerUI((options) =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.yaml", "NL2EF v1");
});

app.UseStaticFiles(new StaticFileOptions()
{
    FileProvider = new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), ".well-known")),
    RequestPath = "/.well-known"
});

app.MapGet("/query", async ([FromServices] IKernel kernel, string question) =>
    {
        var context = kernel.CreateNewContext();
        var function = kernel.Skills.GetFunction(nameof(NimbusQueryPlugin), nameof(NimbusQueryPlugin.Query));

        context.Variables.Update(question);
        context = await function.InvokeAsync(context);

        return context.Result;
        // var context = kernel.CreateNewContext();
        //
        // context.Variables.Update(question);
        //
        // return context.Result;
    }).WithName("Query")
    .WithOpenApi((operation) =>
    {
        operation.Description =
            "Translates a question into SQL, fetches relevant data from the database, and formulates a response based on the retrieved data.";

        var parameter = operation.Parameters[0];

        parameter.Description = "The question to query the database for";

        return operation;
    });

app.Run();