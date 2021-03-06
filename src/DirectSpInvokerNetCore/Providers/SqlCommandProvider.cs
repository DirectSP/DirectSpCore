﻿using DirectSp.Exceptions;
using DirectSp.ProcedureInfos;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace DirectSp.Providers
{
    public class SqlCommandProvider : ICommandProvider
    {
        public string ConnectionStringReadOnly { get; }
        public string ConnectionStringReadWrite { get; }

        public SqlCommandProvider(string connectionString)
        {
            ConnectionStringReadOnly = new SqlConnectionStringBuilder(connectionString) { ApplicationIntent = ApplicationIntent.ReadOnly }.ToString();
            ConnectionStringReadWrite = new SqlConnectionStringBuilder(connectionString) { ApplicationIntent = ApplicationIntent.ReadWrite }.ToString();
        }

        public async Task<CommandResult> Execute(SpInfo procInfo, DirectSpContext context, IDictionary<string, object> callParams, bool isReadScale)
        {
            var res = new CommandResult();

            //set context param if exists
            var contextSpParam = procInfo.Params.FirstOrDefault(x => x.ParamName.Equals("context", StringComparison.InvariantCultureIgnoreCase));
            if (contextSpParam != null)
                callParams.Add(contextSpParam.ParamName, JsonConvert.SerializeObject(context));

            using (var sqlConnection = new SqlConnection(isReadScale ? ConnectionStringReadOnly : ConnectionStringReadWrite))
            using (var sqlCommand = new SqlCommand($"{procInfo.SchemaName}.{procInfo.ProcedureName}", sqlConnection))
            {
                // create SqlParameters
                foreach (var callParam in callParams)
                {
                    var spParam = procInfo.Params.First(x => x.ParamName.Equals(callParam.Key, StringComparison.OrdinalIgnoreCase));
                    var sqlParam = new SqlParameter($"@{spParam.ParamName}", Enum.Parse<SqlDbType>(spParam.SystemTypeName, true), spParam.Length)
                    {
                        Direction = spParam.IsOutput ? ParameterDirection.InputOutput : ParameterDirection.Input
                    };
                    if (callParam.Value != Undefined.Value) sqlParam.Value = callParam.Value;
                    if (callParam.Value == Undefined.Value) sqlParam.Direction = ParameterDirection.Output;
                    if (callParam.Key == "returnValue") sqlParam.Direction = ParameterDirection.ReturnValue;
                    sqlCommand.Parameters.Add(sqlParam);
                }

                sqlCommand.CommandType = CommandType.StoredProcedure;
                if (procInfo.ExtendedProps.CommandTimeout != -1)
                    sqlCommand.CommandTimeout = procInfo.ExtendedProps.CommandTimeout;

                //execute reader
                sqlConnection.Open();
                try
                {
                    using var dataReader = await sqlCommand.ExecuteReaderAsync();
                    res.Table = GetResultTable(dataReader);
                    dataReader.Close();
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                    throw;
                }

                //return out parameter
                res.AgentContext = context.AgentContext;
                for (var i = 0; i < sqlCommand.Parameters.Count; i++)
                {
                    var sqlParameter = sqlCommand.Parameters[i];
                    if (sqlParameter.Direction == ParameterDirection.InputOutput || sqlParameter.Direction == ParameterDirection.Output || sqlParameter.Direction == ParameterDirection.ReturnValue)
                    {
                        //process context param
                        if (sqlParameter.ParameterName.Equals("@context", StringComparison.OrdinalIgnoreCase))
                        {
                            if (sqlParameter.Value != DBNull.Value)
                            {
                                var newContext = JsonConvert.DeserializeObject<DirectSpContext>((string)sqlParameter.Value);
                                res.AgentContext = newContext.AgentContext;
                            }
                        }
                        //process non context param
                        else
                        {
                            res.OutParams.Add(sqlParameter.ParameterName.Substring(1), sqlParameter.Value != DBNull.Value ? sqlParameter.Value : null);
                        }
                    }
                }
            }

            return res;
        }

        private CommandResultTable GetResultTable(SqlDataReader dataReader)
        {
            if (dataReader.FieldCount == 0)
                return null;

            // return fields info
            var fields = new List<CommandResultField>();
            for (int i = 0; i < dataReader.FieldCount; i++)
            {
                fields.Add(new CommandResultField()
                {
                    Name = dataReader.GetName(i),
                    TypeName = Util.GetFriendlySqlTypeName(dataReader.GetProviderSpecificFieldType(i).Name),
                });
            }
            var resultTable = new CommandResultTable
            {
                Fields = fields.ToArray()
            };

            //retrurn recordset
            var data = new List<object[]>(100000);
            while (dataReader.Read())
            {
                var row = new object[dataReader.FieldCount];
                for (int i = 0; i < dataReader.FieldCount; i++)
                    row[i] = dataReader.GetValue(i) == DBNull.Value ? null : dataReader.GetValue(i);
                data.Add(row);
            }

            resultTable.Data = data.ToArray();
            return resultTable;
        }

        public async Task<SpSystemApiInfo> GetSystemApi()
        {
            using var sqlConnection = new SqlConnection(ConnectionStringReadOnly);
            using var sqlCommand = new SqlCommand("api.System_api", sqlConnection);
            var sqlParameters = new List<SqlParameter>()
                {
                    new SqlParameter("@context",SqlDbType.NVarChar, -1) { Direction = ParameterDirection.InputOutput, Value = "$$" },
                    new SqlParameter("@api", SqlDbType.NVarChar, -1) { Direction = ParameterDirection.Output},
                };

            //create command and run it
            sqlCommand.CommandType = CommandType.StoredProcedure;
            sqlCommand.Parameters.AddRange(sqlParameters.ToArray());

            sqlConnection.Open();

            try
            {
                await sqlCommand.ExecuteNonQueryAsync();
            }
            catch (SqlException ex)
            {
                HandleException(ex);
                throw;
            }

            var api = sqlParameters.Find(x => x.ParameterName == "@api").Value as string;
            api = api.Replace("'sql_variant'", "'variant'");
            var ret = new SpSystemApiInfo
            {
                ProcInfos = JsonConvert.DeserializeObject<SpInfo[]>(api),
                AppName = sqlParameters.Find(x => x.ParameterName == "@appName").Value as string,
                AppVersion = sqlParameters.Find(x => x.ParameterName == "@appVersion").Value as string //context
            };

            //remove @ from param names and add return values
            foreach (var procInfo in ret.ProcInfos)
            {
                foreach (var paramInfo in procInfo.Params)
                    paramInfo.ParamName = paramInfo.ParamName.Substring(1);

                //add return value
                var paramList = procInfo.Params.ToList();
                paramList.Add(new SpParamInfo() { IsOutput = true, ParamName = "returnValue", SystemTypeName = "int", UserTypeName = "int", Length = 4 });
                procInfo.Params = paramList.ToArray();
            }

            return ret;
        }

        private void HandleException(Exception ex)
        {
            if (!(ex is SqlException))
                return;

            SpCallError spCallError;
            try
            {
                spCallError = JsonConvert.DeserializeObject<SpCallError>(ex.Message);
            }
            catch
            {
                return;
            }

            if (spCallError.ErrorId != 0 || !string.IsNullOrEmpty(spCallError.ErrorName))
                throw new DirectSpException(spCallError, ex);
        }

    }
}
