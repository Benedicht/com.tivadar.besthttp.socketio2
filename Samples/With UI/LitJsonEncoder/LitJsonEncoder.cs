using System.Collections.Generic;

using Best.HTTP.JSON.LitJson;

namespace Best.SocketIO.JsonEncoders
{
    /// <summary>
    /// This IJsonEncoder implementation uses the LitJson library located in the Examples\LitJson directory.
    /// </summary>
    public sealed class LitJsonEncoder : IJsonEncoder
    {
        public List<object> Decode(string json)
        {
            JsonReader reader = new JsonReader(json);
            return JsonMapper.ToObject<List<object>>(reader);
        }

        public string Encode(List<object> obj)
        {
            JsonWriter writer = new JsonWriter();
            JsonMapper.ToJson(obj, writer);

            return writer.ToString();
        }
    }
}
