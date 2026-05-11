using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;

namespace TLSP_Test;

internal static class SerialPortEnumerator
{
    public static IReadOnlyList<string> List()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                var cu = Directory.GetFiles("/dev", "cu.*");
                var tty = Directory.GetFiles("/dev", "tty.*");
                return cu.Concat(tty).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            }
            catch
            {
                // fall through
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                return Directory.GetFiles("/dev", "ttyUSB*")
                    .Concat(Directory.GetFiles("/dev", "ttyACM*"))
                    .Concat(Directory.GetFiles("/dev", "ttyS*"))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToArray();
            }
            catch
            {
                // fall through
            }
        }

        return SerialPort.GetPortNames();
    }
}
