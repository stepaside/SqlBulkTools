using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("SqlBulkTools.UnitTests")]
[assembly: InternalsVisibleTo("SqlBulkTools.IntegrationTests")]
namespace SqlBulkTools
{
    internal class BulkOperationsHelpers
    {
        internal struct PrecisionType
        {
            public string NumericPrecision { get; set; }
            public string NumericScale { get; set; }
        }

        internal string BuildCreateTempTable(HashSet<string> columns, DataTable schema, bool? outputIdentity = null)
        {
            var actualColumns = new Dictionary<string, string>();
            var actualColumnsMaxCharLength = new Dictionary<string, string>();
            var actualColumnsPrecision = new Dictionary<string, PrecisionType>();
            
            foreach (DataRow row in schema.Rows)
            {
                string columnType = row["DATA_TYPE"].ToString();
                string columnName = row["COLUMN_NAME"].ToString();

                actualColumns.Add(row["COLUMN_NAME"].ToString(), row["DATA_TYPE"].ToString());

                if (columnType == "varchar" || columnType == "nvarchar" || 
                    columnType == "char" || columnType == "binary" || 
                    columnType == "varbinary")

                {
                    actualColumnsMaxCharLength.Add(row["COLUMN_NAME"].ToString(),
                        row["CHARACTER_MAXIMUM_LENGTH"].ToString());
                }

                if (columnType == "numeric" || columnType == "decimal")
                {
                    var p = new PrecisionType
                    {
                        NumericPrecision = row["NUMERIC_PRECISION"].ToString(),
                        NumericScale = row["NUMERIC_SCALE"].ToString()
                    };
                    actualColumnsPrecision.Add(columnName, p);
                }

            }

            var command = new StringBuilder();

            command.Append("CREATE TABLE #TmpTable(");

            List<string> paramList = new List<string>();

            foreach (var column in columns.ToList())
            {
                if (column == "InternalId") continue;
                if (actualColumns.TryGetValue(column, out var columnType))
                {
                    columnType = GetVariableCharType(column, columnType, actualColumnsMaxCharLength);
                    columnType = GetDecimalPrecisionAndScaleType(column, columnType, actualColumnsPrecision);
                }

                paramList.Add("[" + column + "]" + " " + columnType);
            }

            string paramListConcatenated = string.Join(", ", paramList);

            command.Append(paramListConcatenated);

            if (outputIdentity.HasValue && outputIdentity.Value)
            {
                command.Append(", [InternalId] int");
            }
            command.Append(");");

            return command.ToString();
        }

        private string GetVariableCharType(string column, string columnType, IReadOnlyDictionary<string, string> actualColumnsMaxCharLength)
        {
            switch (columnType)
            {
                case "varchar":
                case "nvarchar":
                {
                    if (actualColumnsMaxCharLength.TryGetValue(column, out var maxCharLength))
                    {
                        if (maxCharLength == "-1")
                        {
                            maxCharLength = "max";
                        }

                        columnType = columnType + "(" + maxCharLength + ")";
                    }

                    break;
                }
                case "char":
                {
                    if (actualColumnsMaxCharLength.TryGetValue(column, out var maxCharLength))
                    {
                        columnType = columnType + "(" + maxCharLength + ")";
                    }

                    break;
                }
            }

            return columnType;
        }

        private string GetDecimalPrecisionAndScaleType(string column, string columnType, IReadOnlyDictionary<string, PrecisionType> actualColumnsPrecision)
        {
            if (columnType == "decimal" || columnType == "numeric")
            {
                if (actualColumnsPrecision.TryGetValue(column, out var p))
                {
                    columnType = columnType + "(" + p.NumericPrecision + ", " + p.NumericScale + ")";
                }
            }

            return columnType;
        }

        internal string BuildJoinConditionsForUpdateOrInsert(string[] updateOn, string sourceAlias, string targetAlias)
        {
            StringBuilder command = new StringBuilder();

            command.Append("ON " + "[" + targetAlias + "]" + "." + "[" + updateOn[0] + "]" + " = " + "[" + sourceAlias + "]" + "." + "[" + updateOn[0] + "]" + " ");

            if (updateOn.Length > 1)
            {
                // Start from index 1 to just append "AND" conditions
                for (int i = 1; i < updateOn.Length; i++)
                {
                    command.Append("AND " + "[" + targetAlias + "]" + "." + "[" + updateOn[i] + "]" + " = " + "[" + sourceAlias + "]" + "." + "[" + updateOn[i] + "]" + " ");
                }
            }

            return command.ToString();
        }

        internal string BuildUpdateSet(HashSet<string> columns, string sourceAlias, string targetAlias, string identityColumn)
        {
            StringBuilder command = new StringBuilder();
            List<string> paramsSeparated = new List<string>();

            command.Append("UPDATE SET ");

            foreach (var column in columns.ToList())
            {
                if (identityColumn != null && column != identityColumn || identityColumn == null)
                {
                    if (column != "InternalId") 
                        paramsSeparated.Add("[" + targetAlias + "]" + "." + "[" + column + "]" + " = " + "[" + sourceAlias + "]" + "." + "[" + column + "]");
                }
            }

            command.Append(string.Join(", ", paramsSeparated) + " ");

            return command.ToString();
        }

        internal string BuildInsertSet(HashSet<string> columns, string sourceAlias, string identityColumn)
        {
            StringBuilder command = new StringBuilder();
            List<string> insertColumns = new List<string>();
            List<string> values = new List<string>();

            command.Append("INSERT (");

            foreach (var column in columns.ToList())
            {
                if (identityColumn != null && column != identityColumn || identityColumn == null)
                {
                    if (column != "InternalId")
                    {
                        insertColumns.Add("[" + column + "]");
                        values.Add("[" + sourceAlias + "]" + "." + "[" + column + "]");
                    }
                }
            }

            command.Append(string.Join(", ", insertColumns));
            command.Append(") values (");
            command.Append(string.Join(", ", values));
            command.Append(")");

            return command.ToString();
        }

        internal string GetPropertyName(Expression method)
        {
            LambdaExpression lambda = method as LambdaExpression;
            if (lambda == null)
                throw new ArgumentNullException(nameof(method));

            MemberExpression memberExpr = null;

            if (lambda.Body.NodeType == ExpressionType.Convert)
            {
                memberExpr =
                    ((UnaryExpression)lambda.Body).Operand as MemberExpression;
            }
            else if (lambda.Body.NodeType == ExpressionType.MemberAccess)
            {
                memberExpr = lambda.Body as MemberExpression;
            }

            if (memberExpr == null)
                throw new ArgumentException("method");

            return memberExpr.Member.Name;
        }

        internal DataTable ToDataTable<T>(IEnumerable<T> items, HashSet<string> columns, Dictionary<string, string> columnMappings, List<string> matchOnColumns = null, bool? outputIdentity = null, Dictionary<int, T> outputIdentityDic = null)
        {
            var dataTable = new DataTable(typeof(T).Name);
            
            var tableColumns = new HashSet<string>(columns);

            if (matchOnColumns != null)
            {
                tableColumns = CheckForAdditionalColumns(tableColumns, matchOnColumns);
            }

            if (outputIdentity.HasValue && outputIdentity.Value)
            {
                tableColumns.Add("InternalId");
                columns.Add("InternalId");
            }

            //Get all the properties
            var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var column in tableColumns.ToList())
            {
                dataTable.Columns.Add(columnMappings.ContainsKey(column) ? columnMappings[column] : column);
            }

            AssignTypes(props, tableColumns, dataTable, outputIdentity);

            var counter = 0;

            foreach (var item in items)
            {
                var values = new List<object>();

                foreach (var column in tableColumns.ToList())
                {
                    if (column == "InternalId")
                    {
                        values.Add(counter);
                        outputIdentityDic.Add(counter, item);
                    }
                    else
                    {
                        foreach (var t in props)
                        {
                            if (t.Name == column && item != null)
                            {
                                values.Add(t.GetValue(item, null));
                            }
                        }
                    }

                }
                counter++;
                dataTable.Rows.Add(values.ToArray());

            }
            return dataTable;
        }

        private void AssignTypes(PropertyInfo[] props, HashSet<string> columns, DataTable dataTable, bool? outputIdentity = null)
        {
            var count = 0;

            foreach (var column in columns.ToList())
            {
                if (column == "InternalId")
                {
                    dataTable.Columns[count].DataType = typeof(int);
                }
                else
                    for (int i = 0; i < props.Length; i++)
                    {
                        if (props[i].Name == column)
                        {
                            dataTable.Columns[count].DataType = Nullable.GetUnderlyingType(props[i].PropertyType) ??
                                                                props[i].PropertyType;
                        }
                    }
                count++;
            }
        }

        internal SqlConnection GetSqlConnection(string connectionName, SqlCredential credentials, SqlConnection connection)
        {
            SqlConnection conn = null;

            if (connection != null)
            {
                conn = connection;
                return conn;
            }

            if (connectionName != null)
            {
                conn = new SqlConnection(ConfigurationManager
                    .ConnectionStrings[connectionName].ConnectionString, credentials);
                return conn;
            }

            throw new InvalidOperationException("Could not create SQL Connection");
        }

        internal string GetFullQualifyingTableName(string databaseName, string schemaName, string tableName)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            sb.Append(databaseName);
            sb.Append("].[");
            sb.Append(schemaName);
            sb.Append("].[");
            sb.Append(tableName);
            sb.Append("]");

            return sb.ToString();
        }


        /// <summary>
        /// If there are MatchOnColumns that don't exist in columns, add to columns.
        /// </summary>
        /// <param name="columns"></param>
        /// <param name="matchOnColumns"></param>
        /// <returns></returns>
        internal HashSet<string> CheckForAdditionalColumns(HashSet<string> columns, List<string> matchOnColumns)
        {
            foreach (var col in matchOnColumns)
            {
                if (!columns.Contains(col))
                {
                    columns.Add(col);
                }
            }

            return columns;
        }

        internal void DoColumnMappings(Dictionary<string, string> columnMappings, HashSet<string> columns,
        List<string> updateOnList)
        {
            if (columnMappings.Count > 0)
            {
                foreach (var column in columnMappings)
                {
                    if (columns.Contains(column.Key))
                    {
                        columns.Remove(column.Key);
                        columns.Add(column.Value);
                    }

                    for (int i = 0; i < updateOnList.ToArray().Length; i++)
                    {
                        if (updateOnList[i] == column.Key)
                        {
                            updateOnList[i] = column.Value;
                        }
                    }
                }
            }
        }

        internal void DoColumnMappings(Dictionary<string, string> columnMappings, HashSet<string> columns)
        {
            if (columnMappings.Count > 0)
            {
                foreach (var column in columnMappings)
                {
                    if (columns.Contains(column.Key))
                    {
                        columns.Remove(column.Key);
                        columns.Add(column.Value);
                    }
                }
            }
        }

        /// <summary>
        /// Advanced Settings for SQLBulkCopy class. 
        /// </summary>
        /// <param name="bulkCopy"></param>
        /// <param name="bulkCopyEnableStreaming"></param>
        /// <param name="bulkCopyBatchSize"></param>
        /// <param name="bulkCopyNotifyAfter"></param>
        /// <param name="bulkCopyTimeout"></param>
        internal void SetSqlBulkCopySettings(SqlBulkCopy bulkCopy, bool bulkCopyEnableStreaming, int? bulkCopyBatchSize, int? bulkCopyNotifyAfter, int bulkCopyTimeout)
        {
            bulkCopy.EnableStreaming = bulkCopyEnableStreaming;

            if (bulkCopyBatchSize.HasValue)
            {
                bulkCopy.BatchSize = bulkCopyBatchSize.Value;
            }

            if (bulkCopyNotifyAfter.HasValue)
            {
                bulkCopy.NotifyAfter = bulkCopyNotifyAfter.Value;
            }

            bulkCopy.BulkCopyTimeout = bulkCopyTimeout;
        }


        /// <summary>
        /// This is used only for the BulkInsert method at this time.  
        /// </summary>
        /// <param name="bulkCopy"></param>
        /// <param name="columns"></param>
        /// <param name="customColumnMappings"></param>
        internal void MapColumns(SqlBulkCopy bulkCopy, HashSet<string> columns, Dictionary<string, string> customColumnMappings)
        {

            foreach (var column in columns.ToList())
            {
                if (customColumnMappings.TryGetValue(column, out var mapping))
                {
                    bulkCopy.ColumnMappings.Add(mapping, mapping);
                }
                else
                {
                    bulkCopy.ColumnMappings.Add(column, column);
                }
            }

        }

        internal HashSet<string> GetAllValueTypeAndStringColumns(Type type)
        {
            HashSet<string> columns = new HashSet<string>();

            //Get all the properties
            PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            for (int i = 0; i < props.Length; i++)
            {
                var type2 = props[i].GetType();
                if (props[i].PropertyType.IsValueType || props[i].PropertyType == typeof(string))
                {
                    columns.Add(props[i].Name);
                }
            }

            return columns;

        }

        internal string GetOutputIdentityCmd(string identityColumn, bool outputIdentity, string tmpTableName, OperationType operation)
        {

            StringBuilder sb = new StringBuilder();
            if (identityColumn == null || !outputIdentity)
            {
                return ("; ");
            }

            sb.Append("OUTPUT Source.InternalId, INSERTED." + identityColumn + " INTO " + tmpTableName + "(InternalId, " + identityColumn + "); ");


            return sb.ToString();
        }

        internal string GetOutputCreateTableCmd(bool outputIdentity, string tmpTableName, OperationType operation)
        {
            if (operation == OperationType.Insert)
                return (outputIdentity ? "CREATE TABLE " + tmpTableName + "(InternalId int, Id int); " : "");

            return string.Empty;
        }

        internal string GetIndexManagementCmd(string action, string table, HashSet<string> disableIndexList, bool disableAllIndexes = false)
        {
            //AND sys.objects.name = 'Books' AND sys.indexes.name = 'IX_Title'
            StringBuilder sb = new StringBuilder();

            if (disableIndexList != null && disableIndexList.Any())
            {
                foreach (var index in disableIndexList)
                {
                    sb.Append(" AND sys.indexes.name = \'");
                    sb.Append(index);
                    sb.Append("\'");
                }
            }

            string cmd = "DECLARE @sql AS VARCHAR(MAX)=''; " +
                                "SELECT @sql = @sql + " +
                                "'ALTER INDEX ' + sys.indexes.name + ' ON ' + sys.objects.name + ' " + action + ";'" +
                                "FROM sys.indexes JOIN sys.objects ON sys.indexes.object_id = sys.objects.object_id " +
                                "WHERE sys.indexes.type_desc = 'NONCLUSTERED' " +
                                "AND sys.objects.type_desc = 'USER_TABLE'" +
                                " AND sys.objects.name = '" + table + "'" + (sb.Length > 0 ? sb.ToString() : "") + "; EXEC(@sql);";

            return cmd;
        }

        /// <summary>
        /// Gets schema information for a table. Used to get SQL type of property. 
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="schema"></param>
        /// <param name="tableName"></param>
        /// <returns></returns>
        internal DataTable GetDatabaseSchema(SqlConnection conn, string schema, string tableName)
        {
            string[] restrictions = new string[4];
            restrictions[0] = conn.Database;
            restrictions[1] = schema;
            restrictions[2] = tableName;
            var dtCols = conn.GetSchema("Columns", restrictions);

            if (dtCols.Rows.Count == 0 && schema != null) throw new InvalidOperationException("Table name '" + tableName + "\' with schema name \'" + schema + "\' not found. Check your setup and try again.");
            if (dtCols.Rows.Count == 0) throw new InvalidOperationException("Table name \'" + tableName + "\' not found. Check your setup and try again.");
            return dtCols;
        }

        internal void InsertToTmpTable(SqlConnection conn, SqlTransaction transaction, DataTable dt, bool bulkCopyEnableStreaming, int? bulkCopyBatchSize, int? bulkCopyNotifyAfter, int bulkCopyTimeout, SqlBulkCopyOptions sqlBulkCopyOptions)
        {
            SqlBulkCopy bulkCopy = null;
            try
            {
                bulkCopy = new SqlBulkCopy(conn, sqlBulkCopyOptions, transaction) {DestinationTableName = "#TmpTable"};

                SetSqlBulkCopySettings(bulkCopy, bulkCopyEnableStreaming, bulkCopyBatchSize, bulkCopyNotifyAfter, bulkCopyTimeout);

                bulkCopy.WriteToServer(dt);
            }
            catch (SqlException ex)
            {
                if (!ProcessSqlException(ex, bulkCopy)) throw;
            }
            finally
            {
                ((IDisposable)bulkCopy)?.Dispose();
            }
        }
        
        internal async Task InsertToTmpTableAsync(SqlConnection conn, SqlTransaction transaction, DataTable dt, bool bulkCopyEnableStreaming, int? bulkCopyBatchSize, int? bulkCopyNotifyAfter, int bulkCopyTimeout, SqlBulkCopyOptions sqlBulkCopyOptions)
        {
            SqlBulkCopy bulkCopy = null;
            try
            {
                bulkCopy = new SqlBulkCopy(conn, sqlBulkCopyOptions, transaction) {DestinationTableName = "#TmpTable"};

                SetSqlBulkCopySettings(bulkCopy, bulkCopyEnableStreaming, bulkCopyBatchSize, bulkCopyNotifyAfter,  bulkCopyTimeout);

                await bulkCopy.WriteToServerAsync(dt);
            }
            catch (AggregateException ex)
            {
                if (ex.InnerException is SqlException sqlEx)
                {
                    if (!ProcessSqlException(sqlEx, bulkCopy)) throw;
                }
                else
                {
                    throw;
                }
            }
            catch (SqlException ex)
            {
                if (!ProcessSqlException(ex, bulkCopy)) throw;
            }
            finally
            {
                ((IDisposable)bulkCopy)?.Dispose();
            }
        }

        private static bool ProcessSqlException(SqlException ex, SqlBulkCopy bulkCopy)
        {
            if (!ex.Message.Contains("Received an invalid column length from the bcp client for colid") || ex.Number != 4815) return false;
            var match = Regex.Match(ex.Message, @"\d+");
            var index = Convert.ToInt32(match.Value) - 1;

            var fi = typeof(SqlBulkCopy).GetField("_sortedColumnMappings", BindingFlags.NonPublic | BindingFlags.Instance);
            var sortedColumns = fi?.GetValue(bulkCopy);
            var items = (object[])sortedColumns?.GetType().GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(sortedColumns);

            if (items == null) return false;

            var itemData = items[index].GetType().GetField("_metadata", BindingFlags.NonPublic | BindingFlags.Instance);
            var metaData = itemData?.GetValue(items[index]);

            var column = metaData?.GetType().GetField("column", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(metaData);
            var length = metaData?.GetType().GetField("length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(metaData);
            throw new FormatException($"Column: {column} contains data with a length greater than: {length}");
        }
    }

    internal static class IndexOperation
    {
        public const string Rebuild = "REBUILD";
        public const string Disable = "DISABLE";
    }

    internal static class Constants
    {
        public const string DefaultSchemaName = "dbo";
    }

    internal enum OperationType
    {
        Insert, InsertOrUpdate
    }
}