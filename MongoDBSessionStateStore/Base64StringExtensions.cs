using System;
using System.IO;
using System.Web;
using System.Web.SessionState;

namespace Quintsys.Web.Providers.MongoDBSessionStateStore
{
    public static class Base64StringExtensions
    {
        /// <summary>
        /// Deserializes a base 64 string to a SessionStateStoreData object.
        /// </summary>
        /// <param name="base64String">The base64 string.</param>
        /// <param name="context">The context.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns></returns>
        public static SessionStateStoreData DeserializeToSessionStateStoreData(this string base64String, HttpContext context, int timeout)
        {
            using (var memoryStream = new MemoryStream(Convert.FromBase64String(base64String)))
            {
                var sessionItems = new SessionStateItemCollection();
                if (memoryStream.Length > 0)
                {
                    using (var reader = new BinaryReader(memoryStream))
                    {
                        sessionItems = SessionStateItemCollection.Deserialize(reader);
                    }
                }

                var sessionStateStoreData = new SessionStateStoreData(sessionItems, SessionStateUtility.GetSessionStaticObjects(context), timeout);
                return sessionStateStoreData;
            }
        }
    }
}