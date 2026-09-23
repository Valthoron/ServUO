using System;
using System.Globalization;
using System.IO;
using System.Text;
using Server;

namespace System
{
    public class ConsoleHook : TextWriter
    {
#if DEBUG
        private static readonly bool _Enabled = false;
#else
        private static readonly bool _Enabled = true;
#endif

        private static readonly bool _LocalTime = Config.Get("Server.LocalTimestamps", false);

        private static Stream m_OldOutput;
        private static bool m_Newline;
        public override Encoding Encoding
        {
            get
            {
                return Encoding.ASCII;
            }
        }
        private string Timestamp
        {
            get
            {
                DateTime now = _LocalTime ? DateTime.Now : DateTime.UtcNow;
                return now.ToString("HH:mm:ss ", CultureInfo.InvariantCulture);
            }
        }
        public static void Initialize()
        {
            if (_Enabled)
            {
                m_OldOutput = Console.OpenStandardOutput();
                Console.SetOut(new ConsoleHook());
                m_Newline = true;
            }
        }

        public override void WriteLine(string value)
        {
            if (m_Newline)
            {
                value = this.Timestamp + value;
            }

            byte[] data = this.Encoding.GetBytes(value);
            m_OldOutput.Write(data, 0, data.Length);
            m_OldOutput.WriteByte(10);
            m_Newline = true;
        }

        public override void Write(string value)
        {
            if (m_Newline)
            {
                value = this.Timestamp + value;
            }

            byte[] data = this.Encoding.GetBytes(value);
            m_OldOutput.Write(data, 0, data.Length);
            m_Newline = false;
        }
    }
}