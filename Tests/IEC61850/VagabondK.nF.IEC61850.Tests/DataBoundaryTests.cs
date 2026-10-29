using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;

/// <summary>혼합 접근 결과는 보존하되 잘못된 코드·개수·참조는 거부하고, 서버 목록/노드 상한과 복구 가능 거절을 구분합니다.</summary>
internal static class DataBoundaryTests
{
    private static void Check(bool ok) { if (!ok) throw new Exception("Data boundary assertion."); }
    internal static void Run()
    {
        for (int mode = 0; mode < 7; mode++)
        {
            var channel = MmsClientTests.Ready(); var client = new Iec61850Client(channel); client.Associate();
            var reference = new MmsVariableReference("D", "I");
            var directory = new MmsDataSet(new MmsNamedVariableList("D", "L$S", false, new[] { reference, reference }));
            byte[] access = mode == 1 ? MmsEncoding.Int(0x80, 12) : mode == 2 ? MmsEncoding.Int(0x80, -1) : MmsEncoding.Int(0x80, 0);
            byte[] data = MmsEncoding.Join(access, mode == 3 ? new byte[0] : MmsEncoding.Int(0x85, 42),
                mode == 4 ? MmsEncoding.Int(0x85, 43) : new byte[0]);
            byte[] returned = mode >= 5 ? MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa1,
                new MmsVariableReference("D", mode == 6 ? "wrong" : "L$S").Name())) : new byte[0];
            channel.Segments(MmsClientTests.ServiceResponse(1, MmsEncoding.Tlv(0xa4, returned, MmsEncoding.Tlv(0xa1, data))), 3);
            if (mode == 0 || mode == 5)
            {
                var results = mode == 0 ? client.ReadMultiple(new[] { reference, reference }) : client.ReadDataSet(directory);
                Check(results[0].ErrorCode == 0 && results[0].Value == null && results[1].Value.Integer == 42 && !client.Faulted);
            }
            else
            {
                bool failed = false;
                try
                {
                    if (mode >= 5) client.ReadDataSet(directory);
                    else client.ReadMultiple(new[] { reference, reference });
                }
                catch (MmsProtocolException) { failed = true; }
                Check(failed && client.Faulted);
            }
        }
        var source = new[] { new MmsServerVariable("D", "I", true) };
        var set = new MmsServerDataSet("D", "L$S", source);
        var original = source[0]; source[0] = new MmsServerVariable("D", "J", false);
        Check(set.GetMember(0) == original);
        var children = new MmsServerVariable[255];
        for (int i = 0; i < children.Length; i++) children[i] = new MmsServerVariable("D", "C" + i, true);
        var large = new MmsServerVariable("D", "Large", children);
        Check(new MmsServerDataSet("D", "L$Exact", new[] { large }).GetMember(0).NodeCount == 256);
        bool oversized = false;
        try { new MmsServerDataSet("D", "L$TooLarge", new[] { large, original }); }
        catch (ArgumentException) { oversized = true; }
        Check(oversized);
        byte[] association = MmsEncoding.Association(4096);
        var serverChannel = ServerTests.ReadyTranscript(association, association.Length);
        var server = new Iec61850ServerSession(serverChannel, new[] { original, large }, dataSets: new[] { set });
        server.Associate(); serverChannel.Output.Clear();
        var tooMany = new byte[17][];
        for (int i = 0; i < tooMany.Length; i++) tooMany[i] = new MmsVariableReference("D", "I").Specification();
        serverChannel.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1),
            MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, tooMany)))));
        Check(server.ProcessRequest() && !server.Faulted);
        byte[] tsdu = serverChannel.LastTsdu();
        var pdv = MmsEncoding.UnwrapPdv(MmsEncoding.Single(tsdu, 4, tsdu.Length - 4, 0x61), 3);
        var error = MmsEncoding.Single(pdv.Buffer, pdv.Offset, pdv.Length, 0xa2).Children();
        Check(error.Read(0x80).Integer() == 1);
        var service = error.Read(0xa2).Children(); error.End();
        var code = service.Read(0xa0).Children(); service.End();
        Check(code.Read(0x83).Integer() == 0); code.End();
        serverChannel.Output.Clear();
        serverChannel.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 2), MmsEncoding.Tlv(0xa4,
            MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0xa0, new MmsVariableReference("D", "Large").Specification(),
                new MmsVariableReference("D", "I").Specification())))));
        Check(server.ProcessRequest() && !server.Faulted);
        tsdu = serverChannel.LastTsdu();
        pdv = MmsEncoding.UnwrapPdv(MmsEncoding.Single(tsdu, 4, tsdu.Length - 4, 0x61), 3);
        error = MmsEncoding.Single(pdv.Buffer, pdv.Offset, pdv.Length, 0xa2).Children();
        Check(error.Read(0x80).Integer() == 2);
        serverChannel.Output.Clear();
        serverChannel.AppendRead(MmsEncoding.ReadRequest(3, MmsEncoding.Identifier("D"), MmsEncoding.Identifier("I")));
        Check(server.ProcessRequest() && !server.Faulted);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Check(server.ProcessReports() == 0);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated);
        server.Close();
        Console.WriteLine("DATA bounds: mixed/access-zero/count/reference mismatch, cloned model list, 17-member recoverable rejection, idle poll allocation=0 PASS.");
    }
}
