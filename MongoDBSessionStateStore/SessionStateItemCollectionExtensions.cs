using System;
using System.IO;
using System.Web.SessionState;

namespace Quintsys.Web.Providers.MongoDBSessionStateStore
{
    public static class SessionStateItemCollectionExtensions
    {
        /// <summary>
        /// Convert the SessionStateItemCollection into a Base64 string.
        /// </summary>
        /// <param name="items">The collection of session state items.</param>
        /// <returns></returns>
        public static string Serialize(this SessionStateItemCollection items)
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
    }
}