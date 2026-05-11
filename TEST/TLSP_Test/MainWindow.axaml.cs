using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MsBox.Avalonia;
using MsBoxEnums = MsBox.Avalonia.Enums;

namespace TLSP_Test;

public partial class MainWindow : Window
{
    private SerialPort? _serialPort;
    private readonly DispatcherTimer _autoReadTimer;
    private readonly StringBuilder _receiveBuffer = new();

    private static readonly Dictionary<string, string> ErrorCodes = new()
    {
        ["00"] = "없음",
        ["01"] = "遇阻回退 (장애물 후퇴)",
        ["02"] = "과전압",
        ["03"] = "저전압",
        ["04"] = "기울기 경보",
        ["05"] = "통신 중단",
        ["06"] = "모터A 과전류",
        ["07"] = "모터B 과전류",
        ["08"] = "모터C 과전류",
        ["09"] = "홀A 무신호",
        ["10"] = "홀B 무신호",
        ["11"] = "홀C 무신호",
        ["12"] = "모터 과열",
        ["13"] = "전원 과전류",
        ["14"] = "자세 타임아웃",
        ["15"] = "컨트롤러 미등록",
        ["16"] = "모터 슬라이드"
    };

    private static readonly string[] ModeNames =
        ["정지", "상승", "하강", "리셋", "메모리 위치 이동", "장애물 후퇴"];

    private static readonly int[] BaudRates = [9600, 4800, 19200, 38400, 57600, 115200];

    public MainWindow()
    {
        InitializeComponent();
        _autoReadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoReadTimer.Tick += (_, _) => SendCommand("Read");
        RefreshPorts();

        foreach (var rate in BaudRates)
            CmbBaudRate.Items.Add(rate);
        CmbBaudRate.SelectedIndex = 0; // 9600 default
    }

    private void RefreshPorts()
    {
        CmbPort.Items.Clear();
        foreach (var port in SerialPortEnumerator.List())
            CmbPort.Items.Add(port);
        if (CmbPort.Items.Count > 0)
            CmbPort.SelectedIndex = 0;
    }

    private void BtnRefreshPorts_Click(object? sender, RoutedEventArgs e) => RefreshPorts();

    private async void BtnConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (CmbPort.SelectedItem == null)
        {
            await ShowMessageAsync("COM 포트를 선택하세요.", "알림");
            return;
        }

        try
        {
            var baudRate = (int)CmbBaudRate.SelectedItem!;
            _serialPort = new SerialPort(CmbPort.SelectedItem.ToString()!, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                NewLine = "\r\n"
            };
            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.Open();

            BtnConnect.IsEnabled = false;
            BtnDisconnect.IsEnabled = true;
            CmbPort.IsEnabled = false;
            CmbBaudRate.IsEnabled = false;
            AppendLog($"[시스템] 연결됨: {_serialPort.PortName} @ {baudRate}bps");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync($"연결 실패: {ex.Message}", "오류");
        }
    }

    private void BtnDisconnect_Click(object? sender, RoutedEventArgs e)
    {
        Disconnect();
    }

    private void Disconnect()
    {
        _autoReadTimer.Stop();
        ChkAutoRead.IsChecked = false;

        if (_serialPort is { IsOpen: true })
        {
            _serialPort.DataReceived -= SerialPort_DataReceived;
            _serialPort.Close();
            _serialPort.Dispose();
            _serialPort = null;
        }

        BtnConnect.IsEnabled = true;
        BtnDisconnect.IsEnabled = false;
        CmbPort.IsEnabled = true;
        CmbBaudRate.IsEnabled = true;
        AppendLog("[시스템] 연결 해제됨");
    }

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null || !_serialPort.IsOpen) return;

        var data = _serialPort.ReadExisting();
        _receiveBuffer.Append(data);

        // Process complete messages (terminated by \r\n)
        var buffer = _receiveBuffer.ToString();
        while (buffer.Contains("\r\n"))
        {
            var idx = buffer.IndexOf("\r\n", StringComparison.Ordinal);
            var message = buffer[..idx];
            buffer = buffer[(idx + 2)..];
            _receiveBuffer.Clear();
            _receiveBuffer.Append(buffer);

            var msg = message;
            Dispatcher.UIThread.Post(() => ProcessResponse(msg));
        }
    }

    private void ProcessResponse(string response)
    {
        if (ChkShowHex.IsChecked == true)
            AppendLog("[RX] " + ToHexString(response + "\r\n"));
        else
            AppendLog("[RX] " + response);

        if (response.StartsWith("Length:"))
        {
            ParseStatusResponse(response["Length:".Length..]);
        }
        else if (response.StartsWith("Target:"))
        {
            var result = response["Target:".Length..];
            AppendLog(result == "0" ? "[상태] 지정 높이 이동 실행 가능" : "[상태] 지정 높이 이동 실행 불가");
        }
        else if (response == "ClearErr OK")
        {
            AppendLog("[상태] 에러 코드 클리어 완료");
        }
    }

    private void ParseStatusResponse(string data)
    {
        if (data.Length < 14)
        {
            AppendLog("[오류] 응답 데이터 길이 부족: " + data.Length);
            return;
        }

        var activated = data[0];
        var mode = data[1];
        var locked = data[2];
        var unit = data[3];
        var errorCode = data[8..10];
        var heightStr = data[10..14];

        TxtActivation.Text = activated == '1' ? "활성" : "휴면";
        var modeIdx = mode - '0';
        TxtMode.Text = modeIdx >= 0 && modeIdx < ModeNames.Length ? ModeNames[modeIdx] : $"알 수 없음({mode})";
        TxtLock.Text = locked == '1' ? "해제" : "잠금";
        TxtUnit.Text = unit == '0' ? "미터법" : "영국식";
        TxtError.Text = ErrorCodes.GetValueOrDefault(errorCode, $"알 수 없음({errorCode})");
        TxtHeight.Text = int.TryParse(heightStr, out var h) ? h.ToString() : heightStr;
    }

    private void SendCommand(string command)
    {
        if (_serialPort is not { IsOpen: true })
        {
            AppendLog("[오류] 시리얼 포트가 연결되지 않았습니다.");
            return;
        }

        try
        {
            var fullCommand = command + "\r\n";
            _serialPort.Write(fullCommand);

            if (ChkShowHex.IsChecked == true)
                AppendLog("[TX] " + ToHexString(fullCommand));
            else
                AppendLog("[TX] " + command);
        }
        catch (Exception ex)
        {
            AppendLog("[오류] 전송 실패: " + ex.Message);
        }
    }

    private void SendHandleCommand(int value)
    {
        var cmd = $"Handle:{value:D3}";
        SendCommand(cmd);
    }

    private async void SendMemoryMove(int bit)
    {
        var value = 1 << bit;
        SendCommand($"Handle:{value:D3}");
        await Task.Delay(50);
        SendCommand("Handle:000");
    }

    private async void SendMemorySave(int bit)
    {
        var value = 1 << bit;
        SendCommand($"Handle:{value:D3}");
        await Task.Delay(2000);
        SendCommand("Handle:000");
    }

    // Movement Controls
    private void BtnUp_Click(object? sender, RoutedEventArgs e) => SendHandleCommand(1);
    private void BtnDown_Click(object? sender, RoutedEventArgs e) => SendHandleCommand(2);
    private void BtnStop_Click(object? sender, RoutedEventArgs e) => SendHandleCommand(0);

    private void BtnReset_Click(object? sender, RoutedEventArgs e)
    {
        SendCommand("Handle:032");
        AppendLog("[정보] 리셋 명령 전송됨. 리셋 완료 후 정지 명령을 전송하세요.");
    }

    // Memory Position - Move
    private void BtnMemMove1_Click(object? sender, RoutedEventArgs e) => SendMemoryMove(2);
    private void BtnMemMove2_Click(object? sender, RoutedEventArgs e) => SendMemoryMove(3);
    private void BtnMemMove3_Click(object? sender, RoutedEventArgs e) => SendMemoryMove(4);

    // Memory Position - Save
    private void BtnMemSave1_Click(object? sender, RoutedEventArgs e) => SendMemorySave(2);
    private void BtnMemSave2_Click(object? sender, RoutedEventArgs e) => SendMemorySave(3);
    private void BtnMemSave3_Click(object? sender, RoutedEventArgs e) => SendMemorySave(4);

    // Target Height
    private async void BtnTargetHeight_Click(object? sender, RoutedEventArgs e)
    {
        var text = TxtTargetHeight.Text?.Trim() ?? string.Empty;
        if (!int.TryParse(text, out var height) || text.Length > 3)
        {
            await ShowMessageAsync("높이 값은 3자리 이하 숫자로 입력하세요.", "입력 오류");
            return;
        }
        SendCommand($"Target:{height:D3}");
    }

    // Status / Error
    private void BtnReadStatus_Click(object? sender, RoutedEventArgs e) => SendCommand("Read");
    private void BtnClearError_Click(object? sender, RoutedEventArgs e) => SendCommand("ClearErr");

    private void ChkAutoRead_Changed(object? sender, RoutedEventArgs e)
    {
        if (ChkAutoRead.IsChecked == true)
            _autoReadTimer.Start();
        else
            _autoReadTimer.Stop();
    }

    // Log
    private void AppendLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        TxtLog.Text = (TxtLog.Text ?? string.Empty) + $"[{timestamp}] {message}\n";
        TxtLog.CaretIndex = TxtLog.Text?.Length ?? 0;
    }

    private void BtnClearLog_Click(object? sender, RoutedEventArgs e) => TxtLog.Text = string.Empty;

    private static string ToHexString(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        return BitConverter.ToString(bytes).Replace("-", " ");
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        Disconnect();
    }

    private static async Task ShowMessageAsync(string message, string title)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(title, message, MsBoxEnums.ButtonEnum.Ok, MsBoxEnums.Icon.Info);
        await box.ShowAsync();
    }
}
