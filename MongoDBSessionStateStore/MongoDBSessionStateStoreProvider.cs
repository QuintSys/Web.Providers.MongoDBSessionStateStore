using System;
using System.Collections.Specialized;
using System.Configuration;
using System.Configuration.Provider;
using System.Globalization;
using System.IO;
using System.Web;
using System.Web.Configuration;
using System.Web.SessionState;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Builders;

namespace Quintsys.Web.Providers.MongoDBSessionStateStore
{
    /// <summary>
    ///     Custom ASP.NET Session State Provider using MongoDB as the state store.
    /// </summary>
    public class MongoDBSessionStateStoreProvider : SessionStateStoreProviderBase
    {
        #region private members and ctor

        private const string DefaultDatabaseName = "SessionState";
        private const string DefaultCollectionName = "Sessions";

        private const string ExceptionMessage = "An exception occurred. Please contact your administrator.";
        private const string EventSource = "MongoSessionStateStore";
        private const string EventLog = "Application";

        private string _applicationName;
        private SessionStateSection _config;
        private ConnectionStringSettings _connectionStringSettings;
        private string _connectionString;
        private string _databaseName;
        private string _collectionName;
        private WriteConcern _writeConcern;
        private bool _writeExceptionsToEventLog;

        /// <summary>
        /// Gets the name of the application.
        /// </summary>
        /// <returns></returns>
        private static string GetApplicationName()
        {
            return System.Web.Hosting.HostingEnvironment.ApplicationVirtualPath;
        }

        /// <summary>
        /// Gets the session state configuration element.
        /// </summary>
        private SessionStateSection GetSessionStateConfigurationElement()
        {
            Configuration webConfiguration = WebConfigurationManager.OpenWebConfiguration(_applicationName);
            return (SessionStateSection)webConfiguration.GetSection("system.web/sessionState");
        }

        /// <summary>
        /// Gets the database and collection names from the configuration.
        /// </summary>
        /// <param name="config">The configuration.</param>
        private void GetDatabaseAndCollectionNames(NameValueCollection config)
        {
            _databaseName = config["databaseName"] ?? DefaultDatabaseName;
            _collectionName = config["collectionName"] ?? DefaultCollectionName;
        }
        
        /// <summary>
        /// Initializes the connection string.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <exception cref="System.Configuration.Provider.ProviderException">Connection string cannot be blank.</exception>
        private void InitializeConnectionString(NameValueCollection config)
        {
            _connectionStringSettings = ConfigurationManager.ConnectionStrings[config["connectionStringName"]];
            if (_connectionStringSettings == null ||
                string.IsNullOrWhiteSpace(_connectionStringSettings.ConnectionString))
            {
                throw new ProviderException("Connection string cannot be blank.");
            }
            _connectionString = _connectionStringSettings.ConnectionString;
        }

        /// <summary>
        /// Initializes the write exceptions to even log flag.
        /// </summary>
        /// <param name="config">The configuration.</param>
        private void InitializeWriteExceptionsToEventLog(NameValueCollection config)
        {
            _writeExceptionsToEventLog = config["writeExceptionsToEventLog"] != null &&
                                        config["writeExceptionsToEventLog"].ToLower() == "true";
        }

        /// <summary>
        /// Initializes the write concern options.
        /// Defaults to fsynch=false, w=0 => Provides acknowledgment of write operations on a standalone mongodb or the primary in a replica set.
        /// replicasToWrite config item comes into use when > 0. This translates to the WriteConcern wValue, by adding 1 to it.
        /// e.g. replicasToWrite = 1 means "I want to wait for write operations to be acknowledged at the primary + {replicasToWrite} replicas"
        /// ref: http://docs.mongodb.org/manual/core/write-operations/#write-concern
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <exception cref="System.Configuration.Provider.ProviderException">Replicas To Write must be a valid integer</exception>
        private void InitializeWriteConcernOptions(NameValueCollection config)
        {
            // Sets whether to wait for an fsync to complete.
            var fsync = config["fsync"] != null &&
                        config["fsync"].ToLower() == "true";

            var wValue = GetWValue(config);

            _writeConcern = new WriteConcern
            {
                FSync = fsync,
                W = WriteConcern.WValue.Parse(wValue)
            };
        }

        /// <summary>
        /// Gets the w value.
        /// The w option confirms that write operations have replicated to the specified number of replica set members, including the primary (w+1).
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <returns></returns>
        /// <exception cref="System.Configuration.Provider.ProviderException">Replicas To Write must be a valid integer</exception>
        private static string GetWValue(NameValueCollection config)
        {
            var replicasToWrite = 0;
            if (config["replicasToWrite"] != null && !int.TryParse(config["replicasToWrite"], out replicasToWrite))
            {
                throw new ProviderException("Replicas to write must be a valid integer.");
            }

            var wValue = "1";
            if (replicasToWrite > 0)
            {
                wValue = (replicasToWrite + 1).ToString(CultureInfo.InvariantCulture);
            }
            return wValue;
        }

        /// <summary>
        /// Gets the session collection.
        /// </summary>
        /// <returns></returns>
        private MongoCollection<BsonDocument> GetSessionCollection()
        {
            return new MongoClient(_connectionString)
                .GetServer()
                .GetDatabase(_databaseName)
                .GetCollection(_collectionName);
        }

        /// <summary>
        /// Serialize is called by the SetAndReleaseItemExclusive method to 
        /// convert the SessionStateItemCollection into a Base64 string to    
        /// be stored in MongoDB.
        /// </summary>
        private static string Serialize(SessionStateItemCollection items)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                if (items != null)
                    items.Serialize(writer);

                writer.Close();

                return Convert.ToBase64String(ms.ToArray());
            }
        }

        /// <summary>
        /// Deserializes the given items into a SessionStateStoreData object.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="serializedItems">The serialized items.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns></returns>
        private static SessionStateStoreData Deserialize(HttpContext context, string serializedItems, int timeout)
        {
            using (var memoryStream = new MemoryStream(Convert.FromBase64String(serializedItems)))
            {
                var sessionItems = new SessionStateItemCollection();
                if (memoryStream.Length > 0)
                {
                    using (var reader = new BinaryReader(memoryStream))
                    {
                        sessionItems = SessionStateItemCollection.Deserialize(reader);
                    }
                }

                var sessionStateStoreData = new SessionStateStoreData(sessionItems,
                    SessionStateUtility.GetSessionStaticObjects(context), timeout);
                return sessionStateStoreData;
            }
        }
        
        /// <summary>
        /// GetSessionStoreItem is called by both the GetItem and 
        /// GetItemExclusive methods. GetSessionStoreItem retrieves the 
        /// session data from the data source. If the lockRecord parameter
        /// is true (in the case of GetItemExclusive), then GetSessionStoreItem
        /// locks the record and sets a new LockId and LockDate.
        /// </summary>        
        /// <param name="lockRecord">if set to <c>true</c> [lock record].</param>
        /// <param name="context">The context.</param>
        /// <param name="id">The identifier.</param>
        /// <param name="locked">if set to <c>true</c> [locked].</param>
        /// <param name="lockAge">The lock age.</param>
        /// <param name="lockId">The lock identifier.</param>
        /// <param name="actionFlags">The action flags.</param>
        /// <returns></returns>
        /// <exception cref="System.Configuration.Provider.ProviderException"></exception>
        private SessionStateStoreData GetSessionStoreItem(bool lockRecord,
            HttpContext context,
            string id,
            out bool locked,
            out TimeSpan lockAge,
            out object lockId,
            out SessionStateActions actionFlags)
        {
            SessionStateStoreData item = null;
            locked = false;
            lockAge = TimeSpan.Zero;
            lockId = null;
            actionFlags = SessionStateActions.None;


            var serializedItems = string.Empty;
            var foundRecord = false;
            var deleteData = false;
            var timeout = 0;
            var utcNow = DateTime.UtcNow;

            try
            {
                MongoCollection sessionCollection = GetSessionCollection();

                if (lockRecord)
                {
                    locked = LockRecordIfNotExpired(sessionCollection, id, utcNow);
                }

                IMongoQuery query = GetSessionByIdQuery(id);
                var results = sessionCollection.FindOneAs<BsonDocument>(query);
                if (results != null)
                {
                    var expires = results["Expires"].ToUniversalTime();
                    if (expires < utcNow)
                    {
                        locked = false;
                        deleteData = true;
                    }
                    else
                    {
                        foundRecord = true;
                    }

                    serializedItems = results["Items"].AsString;
                    lockId = results["LockId"].AsInt32;
                    lockAge = utcNow.Subtract(results["LockDate"].ToUniversalTime());
                    actionFlags = (SessionStateActions)results["Flags"].AsInt32;
                    timeout = results["Timeout"].AsInt32;
                }

                if (deleteData)
                    sessionCollection.Remove(query, _writeConcern);

                if (!foundRecord)
                    locked = false;

                if (foundRecord && !locked)
                {
                    lockId = (int) lockId + 1;
                    SetLockIdAndActionFlag(sessionCollection, lockId, query);
                    item = GetOrCreateItem(context, actionFlags, serializedItems, timeout);
                }
            }
            catch (Exception exception)
            {
                if (!_writeExceptionsToEventLog)
                    throw;

                exception.WriteToEventLog("GetSessionStoreItem", EventSource, EventLog);
                throw new ProviderException(ExceptionMessage);
            }

            return item;
        }

        /// <summary>
        /// Gets the or create item based on the action flag parameter.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="actionFlag">The action flag.</param>
        /// <param name="serializedItems">The serialized items.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns></returns>
        private SessionStateStoreData GetOrCreateItem(HttpContext context, SessionStateActions actionFlag, string serializedItems, int timeout)
        {
            return actionFlag == SessionStateActions.InitializeItem
                ? CreateNewStoreData(context, (int) _config.Timeout.TotalMinutes)
                : Deserialize(context, serializedItems, timeout);
        }

        /// <summary>
        /// Updates the lock identifier and set action flag.
        /// </summary>
        /// <param name="sessionCollection">The session collection.</param>
        /// <param name="lockId">The lock identifier.</param>
        /// <param name="query">The query.</param>
        private void SetLockIdAndActionFlag(MongoCollection sessionCollection, object lockId, IMongoQuery query)
        {
            var update = Update.Set("LockId", (int) lockId);
            update.Set("Flags", 0);
            sessionCollection.Update(query, update, _writeConcern);
        }

        /// <summary>
        /// Locks the record if not expired.
        /// </summary>
        /// <param name="sessionCollection">The session collection.</param>
        /// <param name="id">The identifier.</param>
        /// <param name="utcNow">The UTC now.</param>
        /// <returns></returns>
        private bool LockRecordIfNotExpired(MongoCollection sessionCollection, string id, DateTime utcNow)
        {
            var query = Query.And(GetSessionByIdQuery(id),
                Query.EQ("Locked", false),
                Query.GT("Expires", utcNow));

            UpdateBuilder update = Update.Set("Locked", true);
            update.Set("LockDate", utcNow);
            WriteConcernResult result = sessionCollection.Update(query, update, _writeConcern);

            return result.DocumentsAffected == 0;
        }

        /// <summary>
        /// Gets the current session item information.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <returns></returns>
        private IMongoQuery GetSessionByIdQuery(string id)
        {
            return Query.And(Query.EQ("_id", id), Query.EQ("ApplicationName", _applicationName));
        }

        /// <summary>
        /// Gets the current session item information by lock identifier.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <param name="lockId">The lock identifier.</param>
        /// <returns></returns>
        private IMongoQuery GetSessionByIdAndLockIdQuery(string id, object lockId)
        {
            return Query.And(Query.EQ("_id", id),
                Query.EQ("ApplicationName", _applicationName),
                Query.EQ("LockId", (Int32)lockId));
        }

        #endregion

        /// <summary>
        /// Initialise the session state store.
        /// </summary>
        /// <param name="name">session state store name. Defaults to "MongoSessionStateStore" if not supplied</param>
        /// <param name="config">configuration settings</param>
        public override void Initialize(string name, NameValueCollection config)
        {
            if (config == null)
                throw new ArgumentNullException("config");

            if (string.IsNullOrWhiteSpace(name))
                name = "MongoDBSessionStateStoreProvider";

            if (String.IsNullOrEmpty(config["description"]))
            {
                config.Remove("description");
                config.Add("description", "MongoDB Session State Store provider");
            }

            base.Initialize(name, config);

            _applicationName = GetApplicationName();
            _config = GetSessionStateConfigurationElement();

            GetDatabaseAndCollectionNames(config);

            InitializeConnectionString(config);
            InitializeWriteExceptionsToEventLog(config);
            InitializeWriteConcernOptions(config);
        }

        /// <summary>
        /// Creates a new <see cref="T:System.Web.SessionState.SessionStateStoreData" /> object to be used for the current request.
        /// </summary>
        /// <param name="context">The <see cref="T:System.Web.HttpContext" /> for the current request.</param>
        /// <param name="timeout">The session-state <see cref="P:System.Web.SessionState.HttpSessionState.Timeout" /> value for the new <see cref="T:System.Web.SessionState.SessionStateStoreData" />.</param>
        /// <returns>
        /// A new <see cref="T:System.Web.SessionState.SessionStateStoreData" /> for the current request.
        /// </returns>
        public override SessionStateStoreData CreateNewStoreData(HttpContext context, int timeout)
        {
            return new SessionStateStoreData(sessionItems: new SessionStateItemCollection(),
                staticObjects: SessionStateUtility.GetSessionStaticObjects(context),
                timeout: timeout);
        }

        /// <summary>
        /// Sets a reference to the <see cref="T:System.Web.SessionState.SessionStateItemExpireCallback" /> delegate for the Session_OnEnd event defined in the Global.asax file.
        /// </summary>
        /// <param name="expireCallback">The <see cref="T:System.Web.SessionState.SessionStateItemExpireCallback" />  delegate for the Session_OnEnd event defined in the Global.asax file.</param>
        /// <returns>
        /// true if the session-state store provider supports calling the Session_OnEnd event; otherwise, false.
        /// </returns>
        public override bool SetItemExpireCallback(SessionStateItemExpireCallback expireCallback)
        {
            // The Session_End event is only suported by the InProc session manager
            return false;
        }

        /// <summary>
        /// Gets the item.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="id">The identifier.</param>
        /// <param name="locked">if set to <c>true</c> [locked].</param>
        /// <param name="lockAge">The lock age.</param>
        /// <param name="lockId">The lock identifier.</param>
        /// <param name="actionFlags">The action flags.</param>
        /// <returns></returns>
        public override SessionStateStoreData GetItem(HttpContext context,
            string id,
            out bool locked,
            out TimeSpan lockAge,
            out object lockId,
            out SessionStateActions actionFlags)
        {
            return GetSessionStoreItem(lockRecord: false,
                context: context,
                id: id,
                locked: out locked,
                lockAge: out lockAge,
                lockId: out lockId,
                actionFlags: out actionFlags);
        }

        /// <summary>
        /// Returns read-only session-state data from the session data store.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Web.SessionState.SessionStateStoreData"/> populated with session values and information from the session data store.
        /// </returns>
        /// <param name="context">The context.</param>
        /// <param name="id">The identifier.</param>
        /// <param name="locked">if set to <c>true</c> [locked].</param>
        /// <param name="lockAge">The lock age.</param>
        /// <param name="lockId">The lock identifier.</param>
        /// <param name="actionFlags">The action flags.</param>
        /// <returns></returns>
        public override SessionStateStoreData GetItemExclusive(HttpContext context,
            string id,
            out bool locked,
            out TimeSpan lockAge,
            out object lockId,
            out SessionStateActions actionFlags)
        {
            return GetSessionStoreItem(lockRecord: true,
                context: context,
                id: id,
                locked: out locked,
                lockAge: out lockAge,
                lockId: out lockId,
                actionFlags: out actionFlags);
        }

        /// <summary>
        /// Adds a new session-state item to the data store.
        /// </summary>
        /// <param name="context">The <see cref="T:System.Web.HttpContext" /> for the current request.</param>
        /// <param name="id">The <see cref="P:System.Web.SessionState.HttpSessionState.SessionID" /> for the current request.</param>
        /// <param name="timeout">The session <see cref="P:System.Web.SessionState.HttpSessionState.Timeout" /> for the current request.</param>
        /// <exception cref="System.Exception"></exception>
        /// <exception cref="System.Configuration.Provider.ProviderException"></exception>
        public override void CreateUninitializedItem(HttpContext context, string id, int timeout)
        {
            var utcNow = DateTime.UtcNow;
            var doc = new BsonDocument
            {
                {"_id", id},
                {"ApplicationName", _applicationName},
                {"Created", utcNow},
                {"Expires", utcNow.AddMinutes(timeout)},
                {"LockDate", utcNow},
                {"LockId", 0},
                {"Timeout", timeout},
                {"Locked", false},
                {"Items", string.Empty},
                {"Flags", (int) SessionStateActions.InitializeItem}
            };

            try
            {
                MongoCollection sessionCollection = GetSessionCollection();

                var result = sessionCollection.Insert(doc, _writeConcern);
                if (result.Ok)
                    return;

                throw new Exception(result.ErrorMessage);
            }
            catch (Exception exception)
            {
                if (!_writeExceptionsToEventLog)
                    throw;

                exception.WriteToEventLog("CreateUninitializedItem", EventSource, EventLog);
                throw new ProviderException(ExceptionMessage);
            }
        }

        /// <summary>
        /// Releases a lock on an item in the session data store.
        /// </summary>
        /// <param name="context">The <see cref="T:System.Web.HttpContext" /> for the current request.</param>
        /// <param name="id">The session identifier for the current request.</param>
        /// <param name="lockId">The lock identifier for the current request.</param>
        /// <exception cref="System.Configuration.Provider.ProviderException"></exception>
        public override void ReleaseItemExclusive(HttpContext context, string id, object lockId)
        {
            var utcNow = DateTime.UtcNow;
            try
            {
                MongoCollection sessionCollection = GetSessionCollection();

                var query = GetSessionByIdAndLockIdQuery(id, lockId);
                UpdateBuilder update = Update.Set("Locked", false);
                update.Set("Expires", utcNow.AddMinutes(_config.Timeout.TotalMinutes));
                sessionCollection.Update(query, update, _writeConcern);
            }
            catch (Exception exception)
            {
                if (!_writeExceptionsToEventLog)
                    throw;

                exception.WriteToEventLog("ReleaseItemExclusive", EventSource, EventLog);
                throw new ProviderException(ExceptionMessage);
            }
        }

        /// <summary>
        /// Updates the session-item information in the session-state data store with values from the current request, and clears the lock on the data.
        /// </summary>
        /// <param name="context">The <see cref="T:System.Web.HttpContext" /> for the current request.</param>
        /// <param name="id">The session identifier for the current request.</param>
        /// <param name="item">The <see cref="T:System.Web.SessionState.SessionStateStoreData" /> object that contains the current session values to be stored.</param>
        /// <param name="lockId">The lock identifier for the current request.</param>
        /// <param name="newItem">true to identify the session item as a new item; false to identify the session item as an existing item.</param>
        /// <exception cref="System.Configuration.Provider.ProviderException"></exception>
        public override void SetAndReleaseItemExclusive(HttpContext context, string id, SessionStateStoreData item,
            object lockId, bool newItem)
        {
            var sessItems = Serialize((SessionStateItemCollection)item.Items);
            var utcNow = DateTime.UtcNow;
            try
            {
                MongoCollection sessionCollection = GetSessionCollection();

                if (newItem)
                {
                    var doc = new BsonDocument()
                        .Add("_id", id)
                        .Add("ApplicationName", _applicationName)
                        .Add("Created", utcNow)
                        .Add("Expires", utcNow.AddMinutes(item.Timeout))
                        .Add("LockDate", utcNow)
                        .Add("LockId", 0)
                        .Add("Timeout", item.Timeout)
                        .Add("Locked", false)
                        .Add("Items", sessItems)
                        .Add("Flags", (int)SessionStateActions.None);

                    sessionCollection.Save(doc, _writeConcern);
                }
                else
                {
                    var query = GetSessionByIdAndLockIdQuery(id, lockId);
                    var update = Update.Set("Expires", utcNow.AddMinutes(item.Timeout));
                    update.Set("Items", sessItems);
                    update.Set("Locked", false);
                    sessionCollection.Update(query, update, _writeConcern);
                }
            }
            catch (Exception exception)
            {
                if (!_writeExceptionsToEventLog)
                    throw;

                exception.WriteToEventLog("SetAndReleaseItemExclusive", EventSource, EventLog);
                throw new ProviderException(ExceptionMessage);
            }
        }

        /// <summary>
        /// Deletes item data from the session data store.
        /// </summary>
        /// <param name="context">The <see cref="T:System.Web.HttpContext" /> for the current request.</param>
        /// <param name="id">The session identifier for the current request.</param>
        /// <param name="lockId">The lock identifier for the current request.</param>
        /// <param name="item">The <see cref="T:System.Web.SessionState.SessionStateStoreData" /> that represents the item to delete from the data store.</param>
        /// <exception cref="System.Configuration.Provider.ProviderException"></exception>
        public override void RemoveItem(HttpContext context, string id, object lockId, SessionStateStoreData item)
        {
            try
            {
                MongoCollection sessionCollection = GetSessionCollection();

                var query = GetSessionByIdAndLockIdQuery(id, lockId);
                sessionCollection.Remove(query, _writeConcern);
            }
            catch (Exception exception)
            {
                if (!_writeExceptionsToEventLog)
                    throw;

                exception.WriteToEventLog("RemoveItem", EventSource, EventLog);
                throw new ProviderException(ExceptionMessage);
            }
        }

        /// <summary>
        /// Updates the expiration date and time of an item in the session data store.
        /// </summary>
        /// <param name="context">The <see cref="T:System.Web.HttpContext" /> for the current request.</param>
        /// <param name="id">The session identifier for the current request.</param>
        /// <exception cref="System.Configuration.Provider.ProviderException"></exception>
        public override void ResetItemTimeout(HttpContext context, string id)
        {
            var utcNow = DateTime.UtcNow;
            try
            {
                MongoCollection sessionCollection = GetSessionCollection();

                var query = GetSessionByIdQuery(id);
                var update = Update.Set("Expires", utcNow.AddMinutes(_config.Timeout.TotalMinutes));
                sessionCollection.Update(query, update, _writeConcern);
            }
            catch (Exception exception)
            {
                if (!_writeExceptionsToEventLog)
                    throw;

                exception.WriteToEventLog("ResetItemTimeout", EventSource, EventLog);
                throw new ProviderException(ExceptionMessage);
            }
        }

        /// <summary>
        /// Called by the <see cref="T:System.Web.SessionState.SessionStateModule" /> object for per-request initialization.
        /// </summary>
        /// <param name="context">The <see cref="T:System.Web.HttpContext" /> for the current request.</param>
        public override void InitializeRequest(HttpContext context)
        {
        }

        /// <summary>
        /// Called by the <see cref="T:System.Web.SessionState.SessionStateModule" /> object at the end of a request.
        /// </summary>
        /// <param name="context">The <see cref="T:System.Web.HttpContext" /> for the current request.</param>
        public override void EndRequest(HttpContext context)
        {
        }

        /// <summary>
        /// Releases all resources used by the <see cref="T:System.Web.SessionState.SessionStateStoreProviderBase" /> implementation.
        /// </summary>
        public override void Dispose()
        {
        }
    }
}