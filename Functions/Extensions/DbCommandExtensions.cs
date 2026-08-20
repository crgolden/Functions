namespace Functions.Extensions;

using System.Data.Common;

public static class DbCommandExtensions
{
    extension(DbCommand command)
    {
        public void AddParam(string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
