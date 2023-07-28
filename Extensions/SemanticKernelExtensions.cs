using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.AI.OpenAI.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AI.OpenAI.TextEmbedding;
using Microsoft.SemanticKernel.Memory;
using Nectar.Nimbus.DbModelCodeFirst.Models;
using SKNimbusDb.Configuration;
using SKNimbusDb.Plugins;

namespace SKNimbusDb.Extensions;

public static class SemanticKernelExtensions
{
    public static IServiceCollection AddSemanticKernel(this IServiceCollection services)
    {
        services.AddSingleton<IMemoryStore, VolatileMemoryStore>();
        services.AddScoped<ISemanticTextMemory>((serviceProvider) =>
        {
            var storage = serviceProvider.GetRequiredService<IMemoryStore>();
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var semanticKernelOptions = configuration.Get<KernelSettings>();
            var openAIApiKey = semanticKernelOptions.ApiKey;
            var textEmbeddingModel = semanticKernelOptions.DeploymentOrModelId;

            var textEmbeddingGeneration = new AzureTextEmbeddingGeneration(textEmbeddingModel,
                semanticKernelOptions.Endpoint, openAIApiKey);
            var semanticTextMemory = new SemanticTextMemory(storage, textEmbeddingGeneration);

            return semanticTextMemory;
        });
        
        services.AddScoped<IKernel>(serviceProvider =>
        {
            var schemaMemoryCollectionName = $"Schema-{nameof(NimbusDBContext)}";

            var logger = serviceProvider.GetRequiredService<ILogger<IKernel>>();
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var memory = serviceProvider.GetRequiredService<ISemanticTextMemory>();
            var semanticKernelOptions = configuration.Get<KernelSettings>();

            var kernel = Kernel.Builder
                .WithLogger(logger)
                .WithMemory(memory)
                .WithAzureChatCompletionService(
                    semanticKernelOptions.DeploymentOrModelId,
                    semanticKernelOptions.Endpoint,
                    semanticKernelOptions.ApiKey) // This will be used when using AI completions
                .WithAzureTextEmbeddingGenerationService(
                    semanticKernelOptions.DeploymentOrModelId,
                    semanticKernelOptions.Endpoint,
                    semanticKernelOptions.ApiKey)
                .Build();

            kernel.ImportSkills(serviceProvider);

            var dbContext = serviceProvider.GetRequiredService<NimbusDBContext>();
            var dbContextSeedTask = Task.Run(async () =>
            {
                var collections = await kernel.Memory.GetCollectionsAsync();

                if (collections.Contains(schemaMemoryCollectionName)) return;

                var dbCreateScript = dbContext.Database.GenerateCreateScript();
                var dbCreateScriptChunks = dbCreateScript.Split("GO",
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var target = dbCreateScriptChunks.Length;
                for (var i = 0; i < target; i++)
                {
                    var chunk = dbCreateScriptChunks[i];

                    await kernel.Memory.SaveInformationAsync(schemaMemoryCollectionName, chunk,
                        i.ToString());
                }
            });

            // These memories should be seeded as part of a preprocessing step, but for the sake of this demo...
            dbContextSeedTask.Wait();

            return kernel;
        });

        return services;
    }

    private static void ImportSkills(this IKernel kernel, IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var semanticKernelOptions = configuration.Get<KernelSettings>();

        var dbContext = serviceProvider.GetRequiredService<NimbusDBContext>();

        if (semanticKernelOptions.SemanticSkillsDirectory is { } semanticSkillsDirectory)
            kernel.ImportSemanticSkills(semanticSkillsDirectory);

        kernel.ImportMainSkill(dbContext);
    }

    private static void ImportSemanticSkills(this IKernel kernel, string skillsDirectory)
    {
        foreach (var subDirectory in Directory.GetDirectories(skillsDirectory))
            try
            {
                kernel.ImportSemanticSkillFromDirectory(skillsDirectory, Path.GetFileName(subDirectory));
            }
            catch (Exception e)
            {
                kernel.Log.LogError("Failed to import skill from {Directory} ({Message})", subDirectory, e.Message);
            }
    }

    private static void ImportMainSkill(this IKernel kernel, NimbusDBContext dbContext)
    {
        var skill = new NimbusQueryPlugin(kernel, dbContext);
        
        kernel.ImportSkill(skill, nameof(NimbusQueryPlugin));
    }
}