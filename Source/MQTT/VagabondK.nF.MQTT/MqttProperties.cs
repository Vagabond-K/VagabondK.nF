using System;
using System.IO;
using System.Text;

namespace VagabondK.nF.MQTT
{
    public sealed partial class MqttConnection
    {
        private static readonly MqttProperty[] NoProperties = new MqttProperty[0];
        private byte[] EncodeProperties(MqttProperty[] properties, MqttPropertyContext context)
        {
            properties = properties ?? NoProperties;
            ValidateProperties(properties, context, false);
            var body = new Body(maximumBody);
            foreach (var property in properties)
            {
                body.Byte((byte)property.Id);
                switch (MqttProperty.Kind(property.Id))
                {
                    case MqttPropertyKind.Byte: body.Byte((byte)property.Number); break;
                    case MqttPropertyKind.TwoByteInteger: body.Word((ushort)property.Number); break;
                    case MqttPropertyKind.FourByteInteger: body.Word((ushort)(property.Number >> 16)); body.Word((ushort)property.Number); break;
                    case MqttPropertyKind.VariableByteInteger: body.Variable((int)property.Number); break;
                    case MqttPropertyKind.Utf8String: Text(property.Text, true, 65535); body.Text(property.Text); break;
                    case MqttPropertyKind.BinaryData: body.Binary(property.Bytes); break;
                    case MqttPropertyKind.Utf8StringPair: Text(property.Text, true, 65535); Text(property.Value, true, 65535); body.Text(property.Text); body.Text(property.Value); break;
                }
            }
            var result = new byte[body.Count];
            Array.Copy(body.Bytes, 5, result, 0, result.Length);
            return result;
        }
        private static void ValidateProperties(MqttProperty[] properties, MqttPropertyContext context, bool inbound)
        {
            for (int i = 0; i < properties.Length; i++)
            {
                MqttPropertyId id = properties[i].Id;
                bool allowed = id == MqttPropertyId.UserProperty;
                switch (context)
                {
                    case MqttPropertyContext.Connect: allowed |= id == MqttPropertyId.SessionExpiryInterval || id == MqttPropertyId.ReceiveMaximum || id == MqttPropertyId.MaximumPacketSize || id == MqttPropertyId.TopicAliasMaximum || id == MqttPropertyId.RequestProblemInformation || id == MqttPropertyId.RequestResponseInformation || id == MqttPropertyId.AuthenticationMethod || id == MqttPropertyId.AuthenticationData; break;
                    case MqttPropertyContext.ConnAck: allowed |= id == MqttPropertyId.SessionExpiryInterval || id == MqttPropertyId.ReceiveMaximum || id == MqttPropertyId.MaximumQos || id == MqttPropertyId.RetainAvailable || id == MqttPropertyId.MaximumPacketSize || id == MqttPropertyId.AssignedClientIdentifier || id == MqttPropertyId.TopicAliasMaximum ||
                        id == MqttPropertyId.ReasonString || id == MqttPropertyId.WildcardSubscriptionAvailable || id == MqttPropertyId.SubscriptionIdentifierAvailable || id == MqttPropertyId.SharedSubscriptionAvailable || id == MqttPropertyId.ServerKeepAlive || id == MqttPropertyId.ResponseInformation || id == MqttPropertyId.ServerReference || id == MqttPropertyId.AuthenticationMethod || id == MqttPropertyId.AuthenticationData; break;
                    case MqttPropertyContext.Publish: allowed |= id == MqttPropertyId.PayloadFormatIndicator || id == MqttPropertyId.MessageExpiryInterval || id == MqttPropertyId.TopicAlias || id == MqttPropertyId.ResponseTopic || id == MqttPropertyId.CorrelationData || id == MqttPropertyId.SubscriptionIdentifier && inbound || id == MqttPropertyId.ContentType; break;
                    case MqttPropertyContext.PubAck: case MqttPropertyContext.PubRec: case MqttPropertyContext.PubRel: case MqttPropertyContext.PubComp: case MqttPropertyContext.SubAck: case MqttPropertyContext.UnsubAck: allowed |= id == MqttPropertyId.ReasonString; break;
                    case MqttPropertyContext.Subscribe: allowed |= id == MqttPropertyId.SubscriptionIdentifier; break;
                    case MqttPropertyContext.Unsubscribe: break;
                    case MqttPropertyContext.Disconnect: allowed |= id == MqttPropertyId.SessionExpiryInterval || id == MqttPropertyId.ReasonString || id == MqttPropertyId.ServerReference && inbound; break;
                    case MqttPropertyContext.Auth: allowed |= id == MqttPropertyId.AuthenticationMethod || id == MqttPropertyId.AuthenticationData || id == MqttPropertyId.ReasonString; break;
                    case MqttPropertyContext.Will: allowed |= id == MqttPropertyId.WillDelayInterval || id == MqttPropertyId.PayloadFormatIndicator || id == MqttPropertyId.MessageExpiryInterval || id == MqttPropertyId.ContentType || id == MqttPropertyId.ResponseTopic || id == MqttPropertyId.CorrelationData; break;
                }
                if (!allowed) throw new ArgumentException("MQTT property not allowed for packet.");
                for (int j = 0; j < i; j++)
                    if (properties[j].Id == id && id != MqttPropertyId.UserProperty && !(id == MqttPropertyId.SubscriptionIdentifier && inbound && context == MqttPropertyContext.Publish))
                        throw new ArgumentException("Duplicate MQTT property.");
                if (id == MqttPropertyId.ResponseTopic) Name(properties[i].Text, false, 65535);
            }
        }
        private static MqttProperty Find(MqttProperty[] properties, MqttPropertyId id)
        {
            if (properties != null) foreach (var property in properties) if (property.Id == id) return property;
            return null;
        }
        private static uint Number(MqttProperty[] properties, MqttPropertyId id, uint fallback)
        { var property = Find(properties, id); return property == null ? fallback : property.Number; }
        private static int VariableSize(int value)
        { return value < 128 ? 1 : value < 16384 ? 2 : value < 2097152 ? 3 : 4; }
        private static int ReadVariable(byte[] bytes, ref int position, int end)
        {
            int value = 0, shift = 0, digits = 0;
            byte digit;
            do
            {
                if (position == end || digits++ == 4) throw new IOException("MQTT property variable integer.");
                digit = bytes[position++]; value |= (digit & 127) << shift; shift += 7;
            } while ((digit & 128) != 0);
            if (digits > 1 && digit == 0) throw new IOException("MQTT nonminimal property integer.");
            return value;
        }
        private static void Require(byte[] bytes, int position, int count, int end)
        { if (position < 0 || count < 0 || position > end || count > end - position || end > bytes.Length) throw new IOException("MQTT truncated property."); }
        private static byte[] ReadBinary(byte[] bytes, ref int position, int end)
        {
            Require(bytes, position, 2, end);
            int length = bytes[position] << 8 | bytes[position + 1]; position += 2;
            Require(bytes, position, length, end);
            byte[] result = new byte[length]; Array.Copy(bytes, position, result, 0, length); position += length;
            return result;
        }
        private static string ReadText(byte[] bytes, ref int position, int end)
        {
            byte[] text = ReadBinary(bytes, ref position, end);
            if (!ValidUtf8(text, 0, text.Length)) throw new IOException("MQTT invalid property UTF-8.");
            return Encoding.UTF8.GetString(text, 0, text.Length);
        }
        private static MqttProperty[] ParseProperties(byte[] bytes, ref int position, MqttPropertyContext context)
        {
            int length = ReadVariable(bytes, ref position, bytes.Length);
            Require(bytes, position, length, bytes.Length);
            int end = position + length, count = 0;
            if (length == 0) return NoProperties;
            var values = new MqttProperty[length / 2 + 1];
            try
            {
                while (position < end)
                {
                    int propertyId = ReadVariable(bytes, ref position, end);
                    if (propertyId > 255) throw new IOException("MQTT unknown property identifier.");
                    MqttPropertyId id = (MqttPropertyId)propertyId;
                    MqttPropertyKind kind = MqttProperty.Kind(id);
                    MqttProperty property;
                    if (kind < MqttPropertyKind.VariableByteInteger)
                    {
                        int size = kind == MqttPropertyKind.Byte ? 1 : kind == MqttPropertyKind.TwoByteInteger ? 2 : 4;
                        Require(bytes, position, size, end);
                        uint value = 0;
                        while (size-- > 0) value = value << 8 | bytes[position++];
                        property = new MqttProperty(id, value);
                    }
                    else if (kind == MqttPropertyKind.VariableByteInteger) property = new MqttProperty(id, (uint)ReadVariable(bytes, ref position, end));
                    else if (kind == MqttPropertyKind.Utf8String) property = new MqttProperty(id, ReadText(bytes, ref position, end));
                    else if (kind == MqttPropertyKind.BinaryData) property = new MqttProperty(id, ReadBinary(bytes, ref position, end));
                    else property = new MqttProperty(ReadText(bytes, ref position, end), ReadText(bytes, ref position, end));
                    values[count++] = property;
                }
                var result = new MqttProperty[count]; Array.Copy(values, result, count);
                ValidateProperties(result, context, true);
                return result;
            }
            catch (ArgumentException) { throw new IOException("MQTT invalid property type/value/context."); }
        }
        private void WriteProperties(byte[] encoded)
        { outgoing.Variable(encoded.Length); outgoing.Raw(encoded); }
    }
}
