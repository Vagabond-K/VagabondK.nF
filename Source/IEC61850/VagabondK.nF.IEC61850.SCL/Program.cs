using System;
using System.IO;
using System.Text;

namespace VagabondK.nF.IEC61850.SCL
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2) { Console.Error.WriteLine("Usage: <input.scl> <output.cs>"); return 2; }
            string temporary = null;
            try
            {
                string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
                if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Input and output must differ.");
                string generated = SclGenerator.GenerateText(File.ReadAllText(input));
                string directory = Path.GetDirectoryName(output);
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, Path.GetRandomFileName());
                File.WriteAllText(temporary, generated, new UTF8Encoding(false));
                File.Move(temporary, output, true); temporary = null;
                Console.WriteLine("Generated static IEC model: " + output); return 0;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is FormatException || error is ArgumentException || error is System.Xml.XmlException)
            { Console.Error.WriteLine(error.Message); return 1; }
            finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
