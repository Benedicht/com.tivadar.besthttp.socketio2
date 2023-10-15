using System.Collections.Generic;
using Best.HTTP.JSON;

namespace Best.SocketIO.JsonEncoders
{
    /// <summary>
    /// The default IJsonEncoder implementation. It's uses the Json class from the Best.HTTP.JSON namespace to encode and decode.
    /// </summary>
    public sealed class DefaultJSonEncoder : IJsonEncoder
    {
        public List<object> Decode(string json)
        {
            return Json.Decode(json) as List<object>;
        }

        public string Encode(List<object> obj)
        {
            return Json.Encode(obj);
        }
    }
}
