using System;
using VagabondK.nF.Web;

internal static class Program
{
    private static int checks;

    private static void Check(bool value, string message)
    {
        checks++;
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void Eq(string actual, string expected, string message)
    {
        checks++;
        if (actual != expected)
            throw new InvalidOperationException(message + " | expected=" + expected + " actual=" + actual);
    }

    private static void Main()
    {
        WriterBasics();
        ReaderBasics();
        ReaderNumbers();
        ReaderErrors();
        RoundTrip();
        Console.WriteLine("Web JSON: " + checks.ToString() + " assertions passed.");
    }

    private static void WriterBasics()
    {
        Eq(new JsonWriter().BeginObject().Prop("ok", true).EndObject().ToString(),
            "{\"ok\":true}", "bool prop");
        Eq(new JsonWriter().BeginObject().Prop("n", 5).Prop("l", 9000000000L).EndObject().ToString(),
            "{\"n\":5,\"l\":9000000000}", "int/long");
        Eq(new JsonWriter().BeginObject().Prop("s", "a\"b\\c\nd").EndObject().ToString(),
            "{\"s\":\"a\\\"b\\\\c\\nd\"}", "string pair escape");
        Eq(JsonWriter.Escape("q\"x"), "q\\\"x", "escape quote");
        Eq(JsonWriter.Escape("a\u0001b"), "a b", "unnamed control to space");
        Eq(JsonWriter.Escape("a\tb"), "a\\tb", "escape tab");
        Eq(JsonWriter.Escape("ok"), "ok", "escape fast path");
        Eq(new JsonWriter().BeginObject().Prop("a").BeginArray().Str("x").Num(2).Bool(false).Null().EndArray().EndObject().ToString(),
            "{\"a\":[\"x\",2,false,null]}", "array values");
        Eq(new JsonWriter().BeginObject().PropRaw("lim", "{\"hi\":1}").EndObject().ToString(),
            "{\"lim\":{\"hi\":1}}", "raw merge");
        Eq(new JsonWriter().BeginObject().PropArr("k", new string[] { "a", "b" }).EndObject().ToString(),
            "{\"k\":[\"a\",\"b\"]}", "prop array");
        Eq(new JsonWriter().BeginObject().Prop("e", (string)null).EndObject().ToString(),
            "{\"e\":\"\"}", "null string value");

        // 중첩 객체 + 컴마 위치 종합 검증
        var w = new JsonWriter(128);
        w.BeginObject().Prop("unit", "nanoGate").Prop("gw");
        w.BeginObject().Prop("tx", 1).Prop("rx", 2).EndObject();
        w.Prop("points");
        w.BeginArray();
        w.BeginObject().Prop("name", "a,b").Prop("val", 3.5 == 3.5).EndObject();
        w.EndArray();
        w.EndObject();
        Eq(w.ToString(),
            "{\"unit\":\"nanoGate\",\"gw\":{\"tx\":1,\"rx\":2},\"points\":[{\"name\":\"a,b\",\"val\":true}]}",
            "nested streaming");

        try
        {
            var deep = new JsonWriter();
            for (int i = 0; i < 10; i++)
                deep.BeginObject().Prop("x");
            deep.EndObject();
            Check(false, "depth guard");
        }
        catch (InvalidOperationException)
        {
            checks++;
        }
    }

    private static void ReaderBasics()
    {
        JsonValue root;
        string err;
        Check(JsonReader.TryParse("{\"ok\":true,\"n\":42,\"s\":\"he\\\"llo\",\"z\":null,\"a\":[1,2,{\"b\":false}]}", out root, out err),
            "parse object: " + err);
        Check(root.Kind == JsonKind.Object, "kind object");
        bool ok;
        Check(root.TryGetBool("ok", out ok) && ok, "get bool");
        long n;
        Check(root.TryGetLong("n", out n) && n == 42, "get long");
        string s;
        Check(root.TryGetString("s", out s) && s == "he\"llo", "get string unescaped");
        Check(root["z"].Kind == JsonKind.Null, "null node");
        JsonValue a = root["a"];
        Check(a.Kind == JsonKind.Array && a.Count == 3, "array meta");
        long two;
        Check(a[1].TryAsLong(out two) && two == 2, "array index");
        bool b;
        Check(a[2].TryGetBool("b", out b) && !b, "nested object in array");
        string[] keys = root.Keys;
        Check(keys != null && keys.Length == 5 && keys[4] == "a", "keys list");
        Check(root["missing"] == null, "missing key null");

        // 빈 컨테이너 / 유니코드 이스케이프 / 슬래시
        Check(JsonReader.TryParse("{\"e\":{},\"l\":[]}", out root, out err), "empty containers: " + err);
        Check(root["e"].Kind == JsonKind.Object && root["e"].Count == 0, "empty object");
        Check(root["l"].Kind == JsonKind.Array && root["l"].Count == 0, "empty array");
        Check(JsonReader.TryParse("\"\\u0041\\u00e9\\/\"", out root, out err) && root.AsString == "Aé/", "unicode escape");

        // 문자열 앞뒤 공백/탭 허용
        Check(JsonReader.TryParse("  { \"a\" : [ 1 , 2 ] , \"b\" : \"x\" }  ", out root, out err), "whitespace: " + err);
        long sum;
        Check(root["a"][0].TryAsLong(out sum) && sum == 1 && root["a"][1].TryAsLong(out sum) && sum == 2, "whitespace array");

        // Parse 예외 경로
        try
        {
            JsonReader.Parse("{bad}");
            Check(false, "Parse should throw");
        }
        catch (FormatException)
        {
            checks++;
        }
        Check(!JsonReader.TryParse(null, out root, out err) && err != null, "null input");
        Check(JsonReader.IsValid("{\"a\":1}") && !JsonReader.IsValid("{\"a\":1,}"), "IsValid");
    }

    private static void ReaderNumbers()
    {
        JsonValue v;
        string err;
        long l;
        double d;

        Check(JsonReader.TryParse("-42", out v, out err) && v.TryAsLong(out l) && l == -42, "negative int");
        Check(JsonReader.TryParse("0", out v, out err) && v.TryAsLong(out l) && l == 0, "zero");
        Check(JsonReader.TryParse("9000000000", out v, out err) && v.TryAsLong(out l) && l == 9000000000L, "long range");
        Check(!JsonReader.TryParse("1.5", out v, out err) || !v.TryAsLong(out l), "decimal not long");
        Check(JsonReader.TryParse("1.5", out v, out err) && v.TryAsDouble(out d) && Math.Abs(d - 1.5) < 1e-12, "decimal double");
        Check(JsonReader.TryParse("-0.25", out v, out err) && v.TryAsDouble(out d) && Math.Abs(d + 0.25) < 1e-12, "negative frac");
        Check(JsonReader.TryParse("1e3", out v, out err) && v.TryAsDouble(out d) && Math.Abs(d - 1000.0) < 1e-9, "exponent");
        Check(JsonReader.TryParse("2.5e-2", out v, out err) && v.TryAsDouble(out d) && Math.Abs(d - 0.025) < 1e-12, "exp negative");
        Check(v.AsString == "2.5e-2", "number raw text preserved");
    }

    private static void ReaderErrors()
    {
        JsonValue v;
        string err;
        Check(!JsonReader.TryParse("{\"a\":1,}", out v, out err) && err != null, "trailing comma: " + err);
        Check(!JsonReader.TryParse("{\"a\":1", out v, out err), "unterminated object");
        Check(!JsonReader.TryParse("[1 2]", out v, out err), "missing comma");
        Check(!JsonReader.TryParse("{\"a\" 1}", out v, out err), "missing colon");
        Check(!JsonReader.TryParse("{\"a\":tru}", out v, out err), "bad literal");
        Check(!JsonReader.TryParse("\"abc", out v, out err), "unterminated string");
        Check(!JsonReader.TryParse("\"a\\qb\"", out v, out err), "bad escape char");
        Check(!JsonReader.TryParse("{} {}", out v, out err), "trailing text");
        Check(!JsonReader.TryParse("[1,", out v, out err), "unterminated array");
        Check(!JsonReader.TryParse("{\"a\":nan}", out v, out err), "nan not json");

        // 깊이 상한: MaxDepth=8 -> 9중 객체는 실패, 8중은 성공
        string open9 = "";
        string close9 = "";
        for (int i = 0; i < 9; i++)
        {
            open9 += "{\"x\":";
            close9 += "}";
        }
        Check(!JsonReader.TryParse(open9 + "1" + close9, out v, out err) && err == "depth", "depth limit: " + err);
        string open8 = "";
        string close8 = "";
        for (int i = 0; i < 8; i++)
        {
            open8 += "{\"x\":";
            close8 += "}";
        }
        Check(JsonReader.TryParse(open8 + "1" + close8, out v, out err), "8 levels ok: " + err);
    }

    private static void RoundTrip()
    {
        // Writer로 만들고 Reader로 되읽는 왕복 계약
        var w = new JsonWriter(256);
        w.BeginObject()
            .Prop("unit", "nanoGate:modbus-to-dnp3")
            .Prop("count", 7)
            .Prop("cache", true);
        w.Prop("rows");
        w.BeginArray();
        for (int i = 0; i < 3; i++)
        {
            w.BeginObject().Prop("name", "AI\\" + i).Prop("value", i * 10).EndObject();
        }
        w.EndArray();
        w.EndObject();

        JsonValue root;
        string err;
        Check(JsonReader.TryParse(w.ToString(), out root, out err), "roundtrip parse: " + err);
        string unit;
        Check(root.TryGetString("unit", out unit) && unit == "nanoGate:modbus-to-dnp3", "roundtrip unit");
        long count;
        Check(root.TryGetLong("count", out count) && count == 7, "roundtrip count");
        Check(root["rows"].Count == 3, "roundtrip rows");
        string name;
        Check(root["rows"][2].TryGetString("name", out name) && name == "AI\\2", "roundtrip escaped name");
        long val;
        Check(root["rows"][1].TryGetLong("value", out val) && val == 10, "roundtrip value");
    }
}
