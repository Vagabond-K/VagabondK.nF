using VagabondK.nF.MMS;
using System;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;

/// <summary>서비스 응답 대기 중의 보고 전달과 명시적 보고 수신을 확인하고 미등록 ID, 잘못된 구성·비트맵·사유 및 노드 수·연속 보고 수 상한 위반을 거부하는지 검사합니다.</summary>
internal static class ReportBoundaryTests
{
    private static void Check(bool ok) { if (!ok) throw new Exception("Report boundary assertion failed."); }
    internal static void Run()
    {
        for (int mode = 0; mode <= 12; mode++)
        {
            var channel = MmsClientTests.Ready(); var client = new Iec61850Client(channel); client.Associate();
            var model = DataServiceTests.Model(); var set = DataServiceTests.Set(model);
            var control = new MmsServerReportControl(DataChecks.Domain, DataChecks.RcbName, "NFREPORT", set);
            channel.Segments(MmsClientTests.ServiceResponse(1,
                MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, control.Variables[0].EncodeValue()))), 3);
            var directory = new MmsDataSet(new MmsNamedVariableList(DataChecks.Domain, DataChecks.SetName, false,
                new[] { new MmsVariableReference(DataChecks.Domain, model[0].Item),
                    new MmsVariableReference(DataChecks.Domain, model[1].Item), new MmsVariableReference(DataChecks.Domain, model[2].Item) }));
            int callbacks = 0;
            new MmsReportClient(client, DataChecks.Domain, DataChecks.RcbName, directory, r => {
                callbacks++; Check(r.GetValue(1).Integer == 42);
                bool blocked = false;
                try { client.Read("D", "I"); } catch (InvalidOperationException) { blocked = true; }
                Check(blocked);
            });
            byte[] report = Report(mode);
            channel.Segments(Wrap(report), 3);
            if (mode == 0)
            {
                channel.Segments(MmsClientTests.ServiceResponse(2,
                    MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, MmsEncoding.Int(0x85, 43)))), 3);
                Check(client.Read("D", "I").Integer == 43 && callbacks == 1 && !client.Faulted);
                channel.Segments(Wrap(Report(0)), 3);
                var snapshot = client.ReceiveReport();
                Check(snapshot.GetValue(1).Integer == 42 && callbacks == 2 && snapshot.GetReason(1) == 4);
                for (int i = 0; i < 33; i++) channel.Segments(Wrap(Report(0)), 100);
                bool burst = false;
                try { client.Read("D", "I"); } catch (MmsProtocolException) { burst = true; }
                Check(burst && client.Faulted && callbacks == 35);
            }
            else
            {
                bool rejected = false;
                try { client.ReceiveReport(); } catch (MmsProtocolException) { rejected = true; }
                Check(rejected && client.Faulted && !client.Associated && callbacks == 0);
            }
        }
        Console.WriteLine("URCB parser: inline/explicit delivery, immutable snapshots, callback reentry, burst/config/bitmap/reason/node/trailing bounds PASS.");
    }
    private static byte[] Wrap(byte[] pdu)
    { return MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, pdu)); }
    private static byte[] Text(string value)
    { return MmsEncoding.Tlv(0x8a, System.Text.Encoding.ASCII.GetBytes(value)); }
    private static byte[] Report(int mode)
    {
        byte[] first = MmsEncoding.Tlv(0x83, new byte[] { 1 });
        if (mode == 12)
        {
            var nodes = new byte[255][];
            for (int i = 0; i < nodes.Length; i++) nodes[i] = first;
            first = MmsEncoding.Tlv(0xa2, nodes);
        }
        byte[] reason = MmsEncoding.Tlv(0x84, new byte[] { 2, mode == 7 ? (byte)0 : mode == 8 ? (byte)16 : (byte)4 });
        byte[] values = MmsEncoding.Join(first, MmsEncoding.Int(0x85, 42), mode == 10 ? new byte[0] : MmsEncoding.Int(0x85, 43));
        byte[] data = MmsEncoding.Join(
            Text(mode == 1 ? "UNKNOWN" : "NFREPORT"),
            MmsEncoding.Tlv(0x84, new byte[] { 6, mode == 2 ? (byte)0x78 : (byte)0x58, 0x80 }),
            MmsEncoding.Int(0x86, mode == 11 ? 256 : 0),
            Text(mode == 4 ? "OTHER/DS" : DataChecks.Domain + "/" + DataChecks.SetName),
            MmsEncoding.Int(0x86, mode == 3 ? 2 : 1),
            MmsEncoding.Tlv(0x84, new byte[] { mode == 5 ? (byte)6 : (byte)5, mode == 6 ? (byte)0 : mode == 5 ? (byte)0xc0 : (byte)0xe0 }),
            values, reason, reason, reason, mode == 9 ? reason : new byte[0]);
        return MmsEncoding.Tlv(0xa3, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa1,
            MmsEncoding.Tlv(0x80, new byte[] { 82, 80, 84 })), MmsEncoding.Tlv(0xa0, data)));
    }
}
