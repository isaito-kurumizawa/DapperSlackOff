﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;

namespace Dapper
{
    public partial class DapperSlackOff : IDapperSlackOff
    {
        private readonly string _connectionString;
        private readonly string[] _creationDateColumns;
        private readonly string[] _updateDateColumn;

        public DapperSlackOff(string connectionString, string[] creationDateColumns, string[] updateDateColumn)
        {
            _connectionString = connectionString;
            _creationDateColumns = creationDateColumns;
            _updateDateColumn = updateDateColumn;
        }

        public SqlConnection GetOpenConnection()
        {
            var connection = new SqlConnection(_connectionString);
            connection.Open();
            return connection;
        }

        public IEnumerable<T> Get<T>(object entity = null, bool logicalOperator = true)
        {
            var conditions = BuildWhere(entity, logicalOperator);
            return Query<T>($"SELECT * FROM {typeof(T).Name} {conditions}", entity);
        }

        public IEnumerable<T1> GetList<T1, T2>(object lists, string keyName = null)
        {
            var type = typeof(T1);
            var keyPropertyName = string.IsNullOrEmpty(keyName) ? GetKeyProperty(type.GetProperties()).Name : keyName;
            var param = new { Lists = (IEnumerable<T2>)lists };
            return Query<T1>($"SELECT * FROM {type.Name} WHERE {keyPropertyName} IN @Lists", param);
        }

        public T Insert<T>(object entity)
        {
            var type = typeof(T);
            var model = GenerateInstance<T>(entity);
            (string columns, string values) = BuildInsert(type.GetProperties());
            var ids = Query<int>($@"INSERT INTO {type.Name} ({columns}) VALUES ({values});
                                SELECT CAST(SCOPE_IDENTITY() AS INT)", model);
            if (ids.Any())
            {
                var keyName = GetKeyProperty(type.GetProperties()).Name;
                return Query<T>($"SELECT * FROM {type.Name} WHERE {keyName}={ids.First()}", entity).FirstOrDefault();
            }

            return default;
        }

        public T Update<T>(object entity)
        {
            var type = typeof(T);
            var key = GetKeyProperty(type.GetProperties());
            var setStatement = BuildUpdateSet<T>(entity.GetType().GetProperties());
            var conditions = $"{key.Name}=@{key.Name}";

            Execute($"UPDATE {type.Name} SET {setStatement} WHERE {conditions}", entity);
            return Query<T>($"SELECT * FROM {type.Name} WHERE {conditions}", entity).FirstOrDefault();
        }

        public int Delete<T>(object entity = null, bool logicalOperator = true)
        {
            var conditions = BuildWhere(entity, logicalOperator);
            return Execute($"DELETE FROM {typeof(T).Name} {conditions}", entity);
        }

        public T CreateOrUpdate<T>(T entity)
        {
            var type = typeof(T);
            var keyProperty = GetKeyProperty(type.GetProperties());
            return CheckPropertyDefaultValue(keyProperty, entity) ? Insert<T>(entity) : Update<T>(entity);
        }

        public IEnumerable<T> Query<T>(string sql, object param = null)
        {
            using var connection = GetOpenConnection();
            return connection.Query<T>(sql, param);
        }

        public int Execute(string sql, object param)
        {
            using var connection = GetOpenConnection();
            return connection.Execute(sql, param, commandType: CommandType.Text);
        }

        private (string columns, string values) BuildInsert(IEnumerable<PropertyInfo> propertyInfo)
        {
            static bool Predicate(CustomAttributeData c) => c.AttributeType.ToString().Contains(nameof(KeyAttribute));
            var properties = propertyInfo.Where(p => CheckBuiltInType(p) && !p.CustomAttributes.Any(Predicate));
            var columns = properties.Select(x => GetValidColumn(x.Name));
            var values = properties.Select(x => _creationDateColumns.Contains(x.Name) ? $"'{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}'" : $"@{x.Name}");
            return (columns: string.Join(",", columns), values: string.Join(",", values));
        }

        private string BuildUpdateSet<T>(IEnumerable<PropertyInfo> properties)
        {
            var type = typeof(T);
            var key = GetKeyProperty(type.GetProperties());
            IEnumerable<string> setValues = new string[] { };

            foreach (var property in properties)
            {
                var value = "";
                if (_updateDateColumn.Contains(property.Name))
                {
                    value = $"{GetValidColumn(property.Name)}='{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}'";
                }
                else if (CheckBuiltInType(property) && !property.Name.Equals(key.Name) && !_creationDateColumns.Contains(property.Name))
                {
                    value = $"{GetValidColumn(property.Name)}=@{property.Name}";
                }
                else
                {
                    continue;
                }

                setValues = setValues.Concat(new[] { value });
            }

            return string.Join(",", setValues);
        }

        private string BuildWhere(object entity, bool logicalOperator = true)
        {
            if (entity is null)
                return null;

            var properties = entity.GetType().GetProperties().Where(p => CheckBuiltInType(p));
            if (!properties.Any())
                return null;

            var columns = properties.Select(p => p.GetValue(entity) is null ? $"{GetValidColumn(p.Name)} IS NULL" : $"{GetValidColumn(p.Name)}=@{p.Name}");
            var conditions = string.Join(logicalOperator ? " AND " : " OR ", columns);
            return $"WHERE {conditions}";
        }

        private string GetValidColumn(string column)
        {
            switch (column.ToUpper())
            {
                case "KEY":
                case "VALUE":
                    return $"\"{column}\"";
                default:
                    return $"{column}";
            }
        }

        private PropertyInfo GetKeyProperty(IEnumerable<PropertyInfo> properties)
        {
            return properties
                    .SingleOrDefault(p => p.CustomAttributes.Any(c => c.AttributeType.ToString().Contains("KeyAttribute")));
        }

        private T GenerateInstance<T>(object entity)
        {
            var model = (T)Activator.CreateInstance(typeof(T));
            foreach (var property in entity.GetType().GetProperties())
            {
                var value = GetObjectValue(entity, property.Name);
                var modelProperty = model.GetType().GetProperties().First(y => y.Name == property.Name);
                modelProperty.SetValue(model, value);
            }
            return model;
        }

        private object GetObjectValue(object entity, string name)
        {
            var property = entity.GetType().GetProperties().Where(x => x.GetIndexParameters().Length == 0 && x.Name.Equals(name));
            return property.Any() ? property.First().GetValue(entity) : null;
        }

        private bool CheckBuiltInType(PropertyInfo property)
        {
            var type = property.GetMethod.ReturnType;
            return type == typeof(byte) || type == typeof(byte?) ||
                    type == typeof(sbyte) || type == typeof(sbyte?) ||
                    type == typeof(short) || type == typeof(short?) ||
                    type == typeof(ushort) || type == typeof(ushort?) ||
                    type == typeof(int) || type == typeof(int?) ||
                    type == typeof(uint) || type == typeof(uint?) ||
                    type == typeof(long) || type == typeof(long?) ||
                    type == typeof(ulong) || type == typeof(ulong?) ||
                    type == typeof(float) || type == typeof(float?) ||
                    type == typeof(double) || type == typeof(double?) ||
                    type == typeof(decimal) || type == typeof(decimal?) ||
                    type == typeof(bool) || type == typeof(bool?) ||
                    type == typeof(string) ||
                    type == typeof(DateTime) || type == typeof(DateTime?) ||
                    type == typeof(byte[]);
        }

        private bool CheckPropertyDefaultValue<T>(PropertyInfo property, T entity)
        {
            var value = property.GetValue(entity);
            var type = value.GetType();

            return value != null && type.IsValueType && value.Equals(Activator.CreateInstance(type));
        }
    }
}
