using VagabondK.nF.MMS;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.SCL;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>SCL 생성 모델의 초기값·DataSet·제어 왕복을 확인하고 미지원 구성은 명확히 거절하는지 검증합니다.</summary>
internal static class SclGeneratorTests
{
    internal static void Run()
    {
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data.scd"));
        string code = SclGenerator.GenerateText(text);
        if (code != SclGenerator.GenerateText(text)) throw new Exception("SCL output is not deterministic.");
        var model = new GeneratedIec61850Model();
        if (model.Variables.Length != 7 || model.DataSets.Length != 1 || model.Reports.Length != 1 ||
            model.Controls.Length != 4 || model.Controls[0].Enabled || model.Controls[3].Enabled ||
            model.Controls[0].ValueKind != MmsControlValueKind.DoublePoint ||
            model.Controls[3].ValueKind != MmsControlValueKind.AnalogFloat32 ||
            model.Controls[3].Model != MmsControlModel.SboEnhanced ||
            model.Variables[0].Item != "GGIO1$ST$Status$stVal" || model.Variables[4].Item != "GGIO1$MX$Analog$mag$f" ||
            model.DataSets[0].GetMember(0) != model.Variables[0] ||
            model.DataSets[0].GetMember(1) != model.Variables[3] ||
            model.DataSets[0].GetMember(2) != model.Variables[4] ||
            model.Reports[0].DataSet != model.DataSets[0])
            throw new Exception("Generated SCL runtime model differs.");
        var channel = new ServerTests.Transcript();
        MmsValue Value(int index)
        {
            byte[] encoded = model.Variables[index].EncodeValue();
            var cursor = new BerCursor(encoded, 0, encoded.Length);
            return MmsValue.Decode(cursor.Read());
        }
        if (!Value(0).Boolean || Value(3).Integer != -123456 || Value(4).FloatingPoint != 12.5 ||
            Iec61850Data.ReadQuality(Value(1)) != IecQuality.Good ||
            Iec61850Data.ReadTimeQuality(Value(2)) != 0x3f || Iec61850Data.ReadDoublePoint(Value(5)) != IecDoublePoint.On ||
            Value(6).Integer != 2)
            throw new Exception("Generated SCL initial values differ.");
        var session = model.CreateSession(channel);
        if (session.Associated || session.Faulted) throw new Exception("Generated SCL session state differs.");
        string[] bad = {
            text.Replace("bType=\"BOOLEAN\"", "bType=\"Enum\""),
            text.Replace("fc=\"ST\" dchg", "fc=\"CO\" dchg"),
            text.Replace("name=\"Status\" type=\"Status\"", "name=\"Status\" type=\"Missing\""),
            text.Replace("name=\"stVal\" bType=\"BOOLEAN\"", "name=\"stVal\" count=\"3\" bType=\"BOOLEAN\""),
            text.Replace("buffered=\"false\"", "buffered=\"true\""),
            text.Replace("indexed=\"false\"", "indexed=\"true\""),
            text.Replace("bufTime=\"0\"", "bufTime=\"10\""),
            text.Replace("intgPd=\"0\"", "intgPd=\"99\""),
            text.Replace("seqNum=\"true\"", "seqNum=\"false\""),
            text.Replace("dupd=\"false\"", "dupd=\"true\""),
            text.Replace("dchg=\"true\"", "dchg=\"false\""),
            text.Replace(" indexed=\"false\"", ""),
            text.Replace("daName=\"stVal\"", "daName=\"missing\""),
            text.Replace("<DataSet name=\"Measurements\">", "<DataSet name=\"Measurements\"><Unknown />"),
            text.Replace("<DAType id=\"Vector\">", "<DAType id=\"Vector\"><BDA name=\"loop\" bType=\"Struct\" type=\"Vector\" />"),
            text.Replace("<DOType id=\"Status\" cdc=\"SPS\">", "<DOType id=\"Status\" cdc=\"SPS\"><SDO name=\"loop\" type=\"Status\" />"),
            text.Replace("<DAI name=\"stVal\">", "<DAI name=\"unknown\">"),
            text.Replace("<LNodeType id=\"Zero\"", "<LNodeType id=\"Zero\" lnClass=\"LLN0\" /><LNodeType id=\"Zero\""),
            text.Replace("<Val>-123456</Val>", "<Val>2147483648</Val>"),
            text.Replace("<Val>12.5</Val>", "<Val>NaN</Val>"),
            text.Replace("<Val>true</Val>", "<Val>notBoolean</Val>"),
            text.Replace("<Val>on</Val>", "<Val>invalid-position</Val>"),
            text.Replace("<Val>automatic</Val>", "<Val>1</Val>"),
            text.Replace("ord=\"2\">automatic", "ord=\"0\">automatic"),
            text.Replace(">automatic</EnumVal>", ">manual</EnumVal>"),
            text.Replace("<Val>sbo-enhanced</Val>", "<Val>status-only</Val>"),
            text.Replace("<Val>2000</Val>", "<Val>60001</Val>"),
            text.Replace("<Val>operate-once</Val>", "<Val>operate-many</Val>"),
            text.Replace("name=\"ctlNum\" bType=\"INT8U\"", "name=\"ctlNum\" bType=\"INT32\""),
            text.Replace("name=\"orIdent\" bType=\"Octet64\"", "name=\"orIdent\" bType=\"VisString64\""),
            text.Replace("name=\"Cancel\" bType=\"Struct\" type=\"BooleanCancel\"", "name=\"Other\" bType=\"Struct\" type=\"BooleanCancel\""),
            text.Replace("<DOType id=\"PositionControl\" cdc=\"DPC\">", "<DOType id=\"PositionControl\" cdc=\"DPC\"><DA name=\"stVal\" bType=\"Dbpos\" fc=\"ST\" />")
        };
        foreach (string invalid in bad)
        {
            if (invalid == text) throw new Exception("SCL rejection fixture did not change input.");
            bool rejected = false;
            try { SclGenerator.GenerateText(invalid); }
            catch (FormatException error) { rejected = error.Message.Contains("SCL line "); }
            if (!rejected) throw new Exception("Unsupported SCL accepted: " + invalid);
        }
        try
        {
            SclGenerator.GenerateText("<!DOCTYPE SCL [<!ENTITY a SYSTEM 'file:///unavailable'>]>" + text.Substring(text.IndexOf("<SCL", StringComparison.Ordinal)));
            throw new Exception("SCL DTD accepted.");
        }
        catch (System.Xml.XmlException) { }
        GeneratedControl(model);
        Console.WriteLine("SCL: C#7.3 model/session, declared Enum, disabled DPC/APC controls, typed wire/Test, strict diagnostics PASS.");
    }
    private static void GeneratedControl(GeneratedIec61850Model model)
    {
        int executions = 0;
        var pending = new MmsControlRequest[model.Controls.Length];
        for (int i = 0; i < model.Controls.Length; i++)
        {
            if (model.Controls[i].Enabled) throw new Exception("Generated control was implicitly enabled.");
            int index = i;
            model.Controls[i].SetHandlers(r => -1, r => { pending[index] = r; executions++; return -1; });
            model.Controls[i].Enabled = true;
        }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var worker = Task.Run(() => {
            using (var socket = listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult())
            {
                var server = model.CreateSession(new MmsSocketChannel(socket));
                try
                {
                    server.Associate();
                    while (server.ProcessRequest())
                    {
                        for (int i = 0; i < model.Controls.Length; i++)
                            if (pending[i] != null)
                            {
                                if (model.Controls[i].HasPendingOperation)
                                    model.Controls[i].Complete(pending[i], true);
                                pending[i] = null;
                            }
                        server.ProcessControlCompletion();
                    }
                }
                finally { server.Close(); }
            }
        });
        try
        {
            using (var socket = VirtualIedFaultTests.Connect(port))
            {
                var client = new Iec61850Client(new MmsSocketChannel(socket)); client.Associate();
                for (int i = 0; i < model.Controls.Length; i++)
                {
                    var target = model.Controls[i];
                    bool analog = target.ValueKind == MmsControlValueKind.AnalogFloat32;
                    var control = new MmsControlClient(client, target.Domain, target.Item, new byte[] { 83 }, valueKind: target.ValueKind);
                    int error;
                    for (int test = 0; test < 2; test++)
                    {
                        bool testing = test != 0, accepted = false;
                        if (control.Model == MmsControlModel.SboNormal && !control.Select()) throw new Exception("Generated SBO select.");
                        if (control.Model == MmsControlModel.SboEnhanced &&
                            !(analog ? control.SelectWithValue(12.5f, 0, out error, testing) : control.SelectWithValue(true, 0, out error, testing)))
                            throw new Exception("Generated SBOw select.");
                        bool ok = (int)control.Model >= 3 ?
                            (analog ? control.OperateAndWait(12.5f, 0, out accepted, out error, testing) :
                                control.OperateAndWait(true, 0, out accepted, out error, testing)) :
                            control.Operate(true, 0, out error, testing);
                        if (!ok || ((int)control.Model >= 3 && !accepted)) throw new Exception("Generated control execution.");
                    }
                    if (analog ? target.AnalogValue != 12.5f : !target.Value) throw new Exception("Generated control state.");
                }
                client.Release();
            }
            worker.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (executions != 4) throw new Exception("Generated Test invoked an execution handler.");
        }
        finally { listener.Stop(); }
    }
}
