using System.ComponentModel;
using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.SkillDefinition;
using Nectar.Nimbus.DbModelCodeFirst.Models;
using SKNimbusDb.Extensions;

namespace SKNimbusDb.Plugins;

public class NimbusQueryPlugin
{
    private readonly IKernel _kernel;
    private readonly NimbusDBContext _context;

    public NimbusQueryPlugin(IKernel kernel, NimbusDBContext context)
    {
        this._kernel = kernel;
        this._context = context;
    }
    
    [SKFunction, SKName(nameof(Query)), Description("Translates a question into SQL, fetches relevant data from the database, and formulates a response based on the retrieved data.")]
    public async Task<SKContext> Query(SKContext context)
    {
        var schemaMemoryCollectionName = $"Schema-{nameof(NimbusDBContext)}";
            
        var functionContext = context.Clone();
        var schemaMemoryChunks = await this._kernel.Memory
            .SearchAsync(
                collection: schemaMemoryCollectionName,
                query: functionContext.Variables.Input,
                limit: 10)
            .ToListAsync();
        
        var schemaChunks = schemaMemoryChunks.Select((memory) => memory.Metadata.Text).ToList();
        var schema = String.Join("\n\n\n", schemaChunks);
        var createQueryFunction = this._kernel.Skills.GetFunction("SqlSkill", "CreateQuery");

        functionContext.Variables.Set("schema", schema);
        functionContext = await createQueryFunction.InvokeAsync(functionContext);

        var sqlQuery = SanitizeBotResponseSql(functionContext.Result);
        var dbConnection = this._context.Database.GetDbConnection();

        if (dbConnection.State != ConnectionState.Open)
        {
            await dbConnection.OpenAsync();
        }

        var maxRetries = 3;

        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                using var dataSet = await dbConnection.QueryMultipleToDataSetAsync(sqlQuery);
                var csvStringBuilder = new StringBuilder();

                foreach (DataTable dataTable in dataSet.Tables)
                {
                    var dataTableCsv = dataTable.GetCsv();

                    csvStringBuilder.AppendLine(dataTableCsv);
                    csvStringBuilder.AppendLine();
                    csvStringBuilder.AppendLine();
                }

                var dataSetCsv = csvStringBuilder.ToString().TrimEnd();
                var qaWithSqlDataFunction = this._kernel.Skills.GetFunction("QASkill", "WithSqlData");
                var qaWithSqlDataFunctionContext = functionContext.Clone();

                qaWithSqlDataFunctionContext.Variables.Set("sql", sqlQuery);
                qaWithSqlDataFunctionContext.Variables.Set("data", dataSetCsv);
                qaWithSqlDataFunctionContext = await qaWithSqlDataFunction.InvokeAsync(qaWithSqlDataFunctionContext);
                functionContext.Variables.Update(qaWithSqlDataFunctionContext.Result);

                break;
            }
            catch (Exception e)
            {
                if (i == maxRetries - 1)
                {
                    throw;
                }

                var fixQueryFunction = this._kernel.Skills.GetFunction("SqlSkill", "FixQuery");
                var fixQueryFunctionContext = functionContext.Clone();

                fixQueryFunctionContext.Variables.Update(sqlQuery);
                fixQueryFunctionContext.Variables.Set("error", e.Message);
                fixQueryFunctionContext = await fixQueryFunction.InvokeAsync(fixQueryFunctionContext);
                sqlQuery = SanitizeBotResponseSql(fixQueryFunctionContext.Result);
            }
        }

        context.Variables.Update(functionContext.Result);

        return context;        
    }
    
    private static string SanitizeBotResponseSql(string sql)
    {
        return sql.TrimStart("```sql").TrimEnd("```").Trim();
    }
}