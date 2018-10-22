﻿using Autofac;
using SWLOR.Game.Server.Data.Contracts;
using SWLOR.Game.Server.Service.Contracts;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection;
namespace SWLOR.Game.Server.Data
{
    /// <summary>
    /// Autofac calls this script automatically at boot-time (because of the IStartable contract).
    /// It will look for the database supplied by the end user. If it doesn't exist, it will create it with the necessary DDL.
    /// It will then grab all migration scripts and run those sequentially until the database is at the current version.
    /// Scripts are located in Data/Migrations/ and all of the sql files should be marked as "Embedded Resource" so the app can pick them up.
    /// </summary>
    public class DatabaseMigrationRunner: IStartable
    {
        private readonly IDataContext _db;
        private readonly IErrorService _error;

        private string _masterConnectionString;
        private string _swlorConnectionString;

        public DatabaseMigrationRunner(
            IDataContext db,
            IErrorService error)
        {
            _db = db;
            _error = error;

            var ip = Environment.GetEnvironmentVariable("SQL_SERVER_IP_ADDRESS");
            var user = Environment.GetEnvironmentVariable("SQL_SERVER_USERNAME");
            var password = Environment.GetEnvironmentVariable("SQL_SERVER_PASSWORD");
            var database = Environment.GetEnvironmentVariable("SQL_SERVER_DATABASE");
            _masterConnectionString = new SqlConnectionStringBuilder()
            {
                DataSource = ip,
                InitialCatalog = "MASTER",
                UserID = user,
                Password = password
            }.ToString();
            _swlorConnectionString = new SqlConnectionStringBuilder()
            {
                DataSource = ip,
                InitialCatalog = database,
                UserID = user,
                Password = password
            }.ToString();

        }
        
        /// <summary>
        /// Returns the folder name containing the sql migration files.
        /// </summary>
        private string FolderName => string.Format("{0}.Data.Migrations", Assembly.GetExecutingAssembly().GetName().Name);

        /// <summary>
        /// This is fired automatically by Autofac. It's the entry point to the migration runner.
        /// </summary>
        public void Start()
        {
            var executingAssembly = Assembly.GetExecutingAssembly();
            Console.WriteLine("Starting database migration runner. This can take a few moments...");

            BuildDatabase();
            ApplyMigrations();
        }

        /// <summary>
        /// Builds the database if it doesn't exist. Nothing happens if there is already a database created.
        /// </summary>
        private void BuildDatabase()
        {
            if (!_db.Database.Exists())
            {
                Console.WriteLine("Database not found. Generating database...");

                using (var connection = new SqlConnection(_masterConnectionString))
                {
                    connection.Open();
                    try
                    {
                        string dbName = Environment.GetEnvironmentVariable("SQL_SERVER_DATABASE");
                        string sql = $@"CREATE DATABASE [{dbName}]";

                        var command = connection.CreateCommand();
                        command.CommandText = sql;
                        command.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ERROR: Unable to create database. Please check your permissions.");
                        _error.LogError(ex);
                        return;
                    }
                }

                using (var connection = new SqlConnection(_swlorConnectionString))
                {
                    Console.WriteLine("Creating tables, procedures, views, etc...");
                    string sql = ReadResourceFile(FolderName + ".Initialization.sql");
                    ExecuteBatchNonQuery(sql, connection);

                    Console.WriteLine("Tables, procedures, views, etc... all created successfully!");
                }


                Console.WriteLine("Database generated!");
            }
        }
        /// <summary> 
        /// Pull back all of the script files located in the Data/Migrations folder.
        /// All files in this directory should be "Embedded Resources".
        /// Ordering is done based on date and version number. 
        /// Follow the established pattern when adding new migration scripts
        /// </summary>
        private void ApplyMigrations()
        {
            var resources = GetScriptResources();
            foreach (var resource in resources)
            {
                ApplyMigrationScript(resource);
            }
        }

        /// <summary>
        /// Gets the actual file name from a full resource file path.
        /// </summary>
        /// <param name="resourceName">The full resource file path.</param>
        /// <returns></returns>
        private string GetFileNameFromScriptResourceName(string resourceName)
        {
            return resourceName.Remove(0, FolderName.Length + 1); // Length of name plus the period afterwards.
        }

        /// <summary>
        /// Retrieves all scripts which the database runner needs to execute.
        /// Only the scripts which haven't been applied to the database will be retrieved.
        /// </summary>
        /// <returns></returns>
        private List<string> GetScriptResources()
        {
            var currentVersion = _db.DatabaseVersions
                .OrderByDescending(o => o.ScriptName).FirstOrDefault();

            var executingAssembly = Assembly.GetExecutingAssembly();

            var fullList = executingAssembly
                .GetManifestResourceNames()
                .Where(r =>
                {
                    return r.StartsWith(FolderName) &&
                           r.EndsWith(".sql") &&
                           r != (FolderName + ".Initialization.sql");
                })
                .OrderBy(o => o)
                .ToList();

            if (currentVersion == null)
                return fullList;

            return fullList.Where(r =>
            {
                var fileName = GetFileNameFromScriptResourceName(r);
                var versionInfo = GetVersionInformation(fileName);

                return versionInfo.Item1 >= currentVersion.VersionDate &&
                       versionInfo.Item2 > currentVersion.VersionNumber;
            }).ToList();
        }


        /// <summary>
        /// Parses a script's name to retrieve the date and version number.
        /// </summary>
        /// <param name="fileName">The file, in yyyy-MM-dd.v format, where y is year, M is month, d is year, and v is version number.</param>
        /// <returns>A tuple containing the date and version number</returns>
        private Tuple<DateTime, int> GetVersionInformation(string fileName)
        {
            // Remove file extension (.sql), split by period denoting date and version number.
            string fileNameNoExtension = Path.GetFileNameWithoutExtension(fileName);
            string[] data = fileNameNoExtension.Split('.');
            
            DateTime fileDate = DateTime.ParseExact(data[0], "yyyy-MM-dd", null);
            int fileVersion = int.Parse(data[1]);

            return new Tuple<DateTime, int>(fileDate, fileVersion);
        }

        /// <summary>
        /// Applies an individual migration script to the database.
        /// </summary>
        /// <param name="resource">The full resource path of the script, including namespace.</param>
        private void ApplyMigrationScript(string resource)
        {
            string fileName = GetFileNameFromScriptResourceName(resource);
            var executingAssembly = Assembly.GetExecutingAssembly();
            Console.WriteLine("Applying migration script: " + resource);

            using (var connection = new SqlConnection(_swlorConnectionString))
            {
                try
                {
                    string sql = ReadResourceFile(resource);

                    if(!string.IsNullOrWhiteSpace(sql))
                    {
                        ExecuteBatchNonQuery(sql, connection);
                    }

                    AddDatabaseVersionRecord(fileName);
                    Console.WriteLine("Migration script '" + resource + "' applied successfully!");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR: Database migration script named '" + resource + "' failed to apply. Canceling database migration process!");
                    _error.LogError(ex, nameof(DatabaseMigrationRunner));
                    return; // Early exit.
                }
            }
        }

        /// <summary>
        /// Retrieves an embedded resource, reads the file, and returns the SQL.
        /// </summary>
        /// <param name="resource">The fulle resource path of the script, including namespace.</param>
        /// <returns>The SQL commands</returns>
        private string ReadResourceFile(string resource)
        {
            var executingAssembly = Assembly.GetExecutingAssembly();
            string sql;

            using (var stream = executingAssembly.GetManifestResourceStream(resource))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    sql = reader.ReadToEnd();
                }
            }

            return sql.Trim();
        }

        /// <summary>
        /// Adds a database version record to the database, preventing this script from running again in the future.
        /// </summary>
        /// <param name="fileName">The file name, WITHOUT namespacing.</param>
        private void AddDatabaseVersionRecord(string fileName)
        {
            var versionInfo = GetVersionInformation(fileName);
            _db.DatabaseVersions.Add(new DatabaseVersion
            {
                DateApplied = DateTime.UtcNow,
                ScriptName = fileName,
                VersionDate = versionInfo.Item1,
                VersionNumber = versionInfo.Item2
            });
            _db.SaveChanges();
        }

        // Code I pulled from StackOverflow: https://stackoverflow.com/questions/40814/execute-a-large-sql-script-with-go-commands
        // Can't execute the entire script at once. You have to split out the "GO"s
        private void ExecuteBatchNonQuery(string sql, SqlConnection conn)
        {
            string sqlBatch = string.Empty;
            SqlCommand cmd = new SqlCommand(string.Empty, conn);
            conn.Open();
            sql += "\nGO";   // make sure last batch is executed.
            try
            {
                foreach (string line in sql.Split(new string[2] { "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.ToUpperInvariant().Trim() == "GO")
                    {
                        cmd.CommandText = sqlBatch;
                        cmd.ExecuteNonQuery();
                        sqlBatch = string.Empty;
                    }
                    else
                    {
                        sqlBatch += line + "\n";
                    }
                }
            }
            finally
            {
                conn.Close();
            }
        }
    }
}