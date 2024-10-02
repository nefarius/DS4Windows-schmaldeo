﻿using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;
using Microsoft.Win32.SafeHandles;
namespace DS4Windows
{
    public class HidDevice : IDisposable
    {
        public enum ReadStatus
        {
            Success = 0,
            WaitTimedOut = 1,
            WaitFail = 2,
            NoDataRead = 3,
            ReadError = 4,
            NotConnected = 5
        }

        private readonly string _description;
        private readonly string _devicePath;
        private readonly string _parentPath;
        private readonly HidDeviceAttributes _deviceAttributes;

        private readonly HidDeviceCapabilities _deviceCapabilities;
        //private bool _monitorDeviceEvents;
        private string serial = null;
        private SafeFileHandle safeReadHandle;
        private FileStream fileStream;
        private bool isOpen;
        private bool isExclusive;
        private const string BLANK_SERIAL = "00:00:00:00:00:00";

        internal HidDevice(string devicePath, string description = null, string parentPath = null)
        {
            _devicePath = devicePath;
            _description = description;
            _parentPath = parentPath;

            try
            {
                var hidHandle = OpenHandle(_devicePath, false, enumerate: true);

                _deviceAttributes = GetDeviceAttributes(hidHandle);
                _deviceCapabilities = GetDeviceCapabilities(hidHandle);

                hidHandle.Close();
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception.Message);
                throw new Exception(string.Format("Error querying HID device '{0}'.", devicePath), exception);
            }
        }

        public SafeFileHandle SafeReadHandle { get => safeReadHandle; private set => safeReadHandle = value; }
        [Obsolete("Causes memory overhead, should be refactored.")]
        public FileStream FileStream { get => fileStream; private set => fileStream = value; }
        public bool IsOpen { get => isOpen; private set => isOpen = value; }
        public bool IsExclusive { get => isExclusive; private set => isExclusive = value; }
        public bool IsConnected { get { return HidDevices.IsConnected(_devicePath); } }
        public string Description { get { return _description; } }
        public HidDeviceCapabilities Capabilities { get { return _deviceCapabilities; } }
        public HidDeviceAttributes Attributes { get { return _deviceAttributes; } }
        public string DevicePath { get { return _devicePath; } }
        public string ParentPath { get => _parentPath; }

        public override string ToString()
        {
            return string.Format("VendorID={0}, ProductID={1}, Version={2}, DevicePath={3}",
                                _deviceAttributes.VendorHexId,
                                _deviceAttributes.ProductHexId,
                                _deviceAttributes.Version,
                                _devicePath);
        }

        public void OpenDevice(bool isExclusive)
        {
            if (IsOpen) return;
            try
            {
                if (SafeReadHandle == null || SafeReadHandle.IsInvalid)
                    SafeReadHandle = OpenHandle(_devicePath, isExclusive, enumerate: false);
            }
            catch (Exception exception)
            {
                IsOpen = false;
                throw new Exception("Error opening HID device.", exception);
            }

            IsOpen = !SafeReadHandle.IsInvalid;
            IsExclusive = isExclusive;
        }

        public void OpenFileStream(int reportSize)
        {
            if (FileStream == null && !SafeReadHandle.IsInvalid)
            {
                FileStream = new FileStream(SafeReadHandle, FileAccess.ReadWrite, reportSize, true);
            }
        }

        public bool IsFileStreamOpen()
        {
            bool result = false;
            if (FileStream != null)
            {
                result = !FileStream.SafeFileHandle.IsInvalid && !FileStream.SafeFileHandle.IsClosed;
            }

            return result;
        }

        public void CloseDevice()
        {
            if (!IsOpen) return;
            closeFileStreamIO();

            IsOpen = false;
        }

        public void Dispose()
        {
            CancelIO();
            CloseDevice();
        }

        public void CancelIO()
        {
            if (IsOpen)
                NativeMethods.CancelIoEx(SafeReadHandle.DangerousGetHandle(), IntPtr.Zero);
        }

        [Obsolete("Unused.")]
        public bool ReadInputReport(byte[] data)
        {
            if (SafeReadHandle == null)
                SafeReadHandle = OpenHandle(_devicePath, true, enumerate: false);
            return NativeMethods.HidD_GetInputReport(SafeReadHandle, data, data.Length);
        }

        public bool WriteFeatureReport(byte[] data)
        {
            bool result = false;
            if (IsOpen && SafeReadHandle != null)
            {
                result = NativeMethods.HidD_SetFeature(SafeReadHandle, data, data.Length);
            }

            return result;
        }


        private static HidDeviceAttributes GetDeviceAttributes(SafeFileHandle hidHandle)
        {
            var deviceAttributes = default(NativeMethods.HIDD_ATTRIBUTES);
            deviceAttributes.Size = Marshal.SizeOf(deviceAttributes);
            NativeMethods.HidD_GetAttributes(hidHandle.DangerousGetHandle(), ref deviceAttributes);
            return new HidDeviceAttributes(deviceAttributes);
        }

        private static HidDeviceCapabilities GetDeviceCapabilities(SafeFileHandle hidHandle)
        {
            var capabilities = default(NativeMethods.HIDP_CAPS);
            var preparsedDataPointer = default(IntPtr);

            if (NativeMethods.HidD_GetPreparsedData(hidHandle.DangerousGetHandle(), ref preparsedDataPointer))
            {
                NativeMethods.HidP_GetCaps(preparsedDataPointer, ref capabilities);
                NativeMethods.HidD_FreePreparsedData(preparsedDataPointer);
            }

            return new HidDeviceCapabilities(capabilities);
        }

        private void closeFileStreamIO()
        {
            if (FileStream != null)
            {
                try
                {
                    FileStream.Close();
                }
                catch (IOException) { }
                catch (OperationCanceledException) { }
            }

            FileStream = null;
            Console.WriteLine("Close fs");
            if (SafeReadHandle != null && !SafeReadHandle.IsInvalid)
            {
                try
                {
                    if (!SafeReadHandle.IsClosed)
                    {
                        SafeReadHandle.Close();
                        Console.WriteLine("Close sh");
                    }
                }
                catch (IOException) { }
            }

            SafeReadHandle = null;
        }

        [Obsolete("Unused.")]
        public void flush_Queue()
        {
            if (SafeReadHandle != null)
            {
                NativeMethods.HidD_FlushQueue(SafeReadHandle);
            }
        }

        private ReadStatus ReadWithFileStreamTask(byte[] inputBuffer)
        {
            try
            {
                if (FileStream.Read(inputBuffer, 0, inputBuffer.Length) > 0)
                {
                    return ReadStatus.Success;
                }
                else
                {
                    return ReadStatus.NoDataRead;
                }
            }
            catch (Exception)
            {
                return ReadStatus.ReadError;
            }
        }

        public unsafe ReadStatus ReadFile(Span<byte> inputBuffer)
        {
            SafeReadHandle ??= OpenHandle(_devicePath, true, false);

            using AutoResetEvent wait = new(false);
            Overlapped overlapped = new Overlapped(0, 0, wait.SafeWaitHandle.DangerousGetHandle(), null);
            NativeOverlapped* packed = overlapped.Pack(null, null);

            if (PInvoke.ReadFile(SafeReadHandle, inputBuffer, null, packed))
                return ReadStatus.Success;

            // NOTE: if a timeout is required, use GetOverlappedResultEx instead
            if (!PInvoke.GetOverlappedResult(SafeReadHandle, *packed, out var transferred, new BOOL(true)))
                return ReadStatus.ReadError;

            // this should never happen
            if (transferred > inputBuffer.Length)
                throw new InvalidOperationException("We read more than the buffer can hold.");

            return ReadStatus.Success;
        }

        [Obsolete("Leaks memory, do not use.")]
        public ReadStatus ReadWithFileStream(byte[] inputBuffer)
        {
            try
            {
                if (FileStream.Read(inputBuffer, 0, inputBuffer.Length) > 0)
                {
                    return ReadStatus.Success;
                }
                else
                {
                    return ReadStatus.NoDataRead;
                }
            }
            catch (Exception)
            {
                return ReadStatus.ReadError;
            }
        }

        public ReadStatus ReadWithFileStream(byte[] inputBuffer, int timeout)
        {
            try
            {
                //if (safeReadHandle == null)
                //    safeReadHandle = OpenHandle(_devicePath, true, enumerate: false);
                //if (fileStream == null && !safeReadHandle.IsInvalid)
                //    fileStream = new FileStream(safeReadHandle, FileAccess.ReadWrite, inputBuffer.Length, true);

                if (!SafeReadHandle.IsInvalid && FileStream.CanRead)
                {
                    Task<ReadStatus> readFileTask = new Task<ReadStatus>(() => ReadWithFileStreamTask(inputBuffer));
                    readFileTask.Start();
                    bool success = readFileTask.Wait(timeout);
                    if (success)
                    {
                        if (readFileTask.Result == ReadStatus.Success)
                        {
                            return ReadStatus.Success;
                        }
                        else if (readFileTask.Result == ReadStatus.ReadError)
                        {
                            return ReadStatus.ReadError;
                        }
                        else if (readFileTask.Result == ReadStatus.NoDataRead)
                        {
                            return ReadStatus.NoDataRead;
                        }
                    }
                    else
                        return ReadStatus.WaitTimedOut;
                }

            }
            catch (Exception e)
            {
                if (e is AggregateException)
                {
                    Console.WriteLine(e.Message);
                    return ReadStatus.WaitFail;
                }
                else
                {
                    return ReadStatus.ReadError;
                }
            }

            return ReadStatus.ReadError;
        }

        public ReadStatus ReadAsyncWithFileStream(byte[] inputBuffer, int timeout)
        {
            try
            {
                //if (safeReadHandle == null)
                //    safeReadHandle = OpenHandle(_devicePath, true, enumerate: false);
                //if (fileStream == null && !safeReadHandle.IsInvalid)
                //    fileStream = new FileStream(safeReadHandle, FileAccess.ReadWrite, inputBuffer.Length, true);

                if (!SafeReadHandle.IsInvalid && FileStream.CanRead)
                {
                    Task<int> readTask = FileStream.ReadAsync(inputBuffer, 0, inputBuffer.Length);
                    bool success = readTask.Wait(timeout);
                    if (success)
                    {
                        if (readTask.Result > 0)
                        {
                            return ReadStatus.Success;
                        }
                        else
                        {
                            return ReadStatus.NoDataRead;
                        }
                    }
                    else
                    {
                        return ReadStatus.WaitTimedOut;
                    }
                }

            }
            catch (Exception e)
            {
                if (e is AggregateException)
                {
                    Console.WriteLine(e.Message);
                    return ReadStatus.WaitFail;
                }
                else
                {
                    return ReadStatus.ReadError;
                }
            }

            return ReadStatus.ReadError;
        }

        public bool WriteOutputReportViaControl(byte[] outputBuffer)
        {
            if (SafeReadHandle == null)
            {
                SafeReadHandle = OpenHandle(_devicePath, true, enumerate: false);
            }

            if (NativeMethods.HidD_SetOutputReport(SafeReadHandle, outputBuffer, outputBuffer.Length))
                return true;
            else
                return false;
        }

        private bool WriteOutputReportViaInterruptTask(byte[] outputBuffer)
        {
            try
            {
                FileStream.Write(outputBuffer, 0, outputBuffer.Length);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return false;
            }
        }

        public bool WriteOutputReportViaInterrupt(byte[] outputBuffer, int timeout)
        {
            try
            {
                //if (safeReadHandle == null)
                //{
                //    safeReadHandle = OpenHandle(_devicePath, true, enumerate: false);
                //}
                //if (fileStream == null && !safeReadHandle.IsInvalid)
                //{
                //    fileStream = new FileStream(safeReadHandle, FileAccess.ReadWrite, outputBuffer.Length, true);
                //}
                if (FileStream != null && FileStream.CanWrite && !SafeReadHandle.IsInvalid)
                {
                    FileStream.Write(outputBuffer, 0, outputBuffer.Length);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

        }

        public bool WriteAsyncOutputReportViaInterrupt(byte[] outputBuffer)
        {
            try
            {
                //if (safeReadHandle == null)
                //{
                //    safeReadHandle = OpenHandle(_devicePath, true, enumerate: false);
                //}
                //if (fileStream == null && !safeReadHandle.IsInvalid)
                //{
                //    fileStream = new FileStream(safeReadHandle, FileAccess.ReadWrite, outputBuffer.Length, true);
                //}

                if (FileStream != null && FileStream.CanWrite && !SafeReadHandle.IsInvalid)
                {
                    Task writeTask = FileStream.WriteAsync(outputBuffer, 0, outputBuffer.Length);
                    //fileStream.Write(outputBuffer, 0, outputBuffer.Length);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

        }

        private SafeFileHandle OpenHandle(string devicePathName, bool isExclusive, bool enumerate)
        {
            return PInvoke.CreateFile(
                devicePathName,
                enumerate
                    ? (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_READ
                    : (uint)(FILE_ACCESS_RIGHTS.FILE_GENERIC_READ | FILE_ACCESS_RIGHTS.FILE_GENERIC_WRITE),
                isExclusive
                    ? 0
                    : FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
                null
            );
        }

        public bool readFeatureData(byte[] inputBuffer)
        {
            return NativeMethods.HidD_GetFeature(SafeReadHandle.DangerousGetHandle(), inputBuffer, inputBuffer.Length);
        }

        public void resetSerial()
        {
            serial = null;
        }

        public string ReadSerial(byte featureID = 18)
        {
            if (serial != null)
                return serial;

            // Some devices don't have MAC address (especially gamepads with USB only suports in PC). If the serial number reading fails 
            // then use dummy zero MAC address, because there is a good chance the gamepad stll works in DS4Windows app (the code would throw
            // an index out of bounds exception anyway without IF-THEN-ELSE checks after trying to read a serial number).

            if (Capabilities.InputReportByteLength == 64)
            {
                byte[] buffer = new byte[64];
                //buffer[0] = 18;
                buffer[0] = featureID;
                if (readFeatureData(buffer))
                    serial = String.Format("{0:X02}:{1:X02}:{2:X02}:{3:X02}:{4:X02}:{5:X02}",
                        buffer[6], buffer[5], buffer[4], buffer[3], buffer[2], buffer[1]);
            }
            else
            {
                byte[] buffer = new byte[126];
#if WIN64
                ulong bufferLen = 126;
#else
                uint bufferLen = 126;
#endif
                if (NativeMethods.HidD_GetSerialNumberString(SafeReadHandle.DangerousGetHandle(), buffer, bufferLen))
                {
                    string MACAddr = System.Text.Encoding.Unicode.GetString(buffer).Replace("\0", string.Empty).ToUpper();
                    if (MACAddr.Length == 12)
                    {
                        MACAddr = $"{MACAddr[0]}{MACAddr[1]}:{MACAddr[2]}{MACAddr[3]}:{MACAddr[4]}{MACAddr[5]}:{MACAddr[6]}{MACAddr[7]}:{MACAddr[8]}{MACAddr[9]}:{MACAddr[10]}{MACAddr[11]}";
                        serial = MACAddr;
                    }
                }
            }

            // If serial# reading failed then generate a dummy MAC address based on HID device path (WinOS generated runtime unique value based on connected usb port and hub or BT channel).
            // The device path remains the same as long the gamepad is always connected to the same usb/BT port, but may be different in other usb ports. Therefore this value is unique
            // as long the same device is always connected to the same usb port.
            if (serial == null)
            {
                AppLogger.LogToGui($"WARNING: Failed to read serial# from a gamepad ({this._deviceAttributes.VendorHexId}/{this._deviceAttributes.ProductHexId}). Generating MAC address from a device path. From now on you should connect this gamepad always into the same USB port or BT pairing host to keep the same device path.", true);
                serial = GenerateFakeHwSerial();
            }

            return serial;
        }

        public string GenerateFakeHwSerial()
        {
            string MACAddr = string.Empty;

            try
            {
                // Substring: \\?\hid#vid_054c&pid_09cc&mi_03#7&1f882A25&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030} -> \\?\hid#vid_054c&pid_09cc&mi_03#7&1f882A25&0&0001#
                int endPos = this.DevicePath.LastIndexOf('{');
                if (endPos < 0)
                    endPos = this.DevicePath.Length;

                // String array: \\?\hid#vid_054c&pid_09cc&mi_03#7&1f882A25&0&0001# -> [0]=\\?\hidvid_054c, [1]=pid_09cc, [2]=mi_037, [3]=1f882A25, [4]=0, [5]=0001
                string[] devPathItems = this.DevicePath.Substring(0, endPos).Replace("#", "").Replace("-", "").Replace("{", "").Replace("}", "").Split('&');

                if (devPathItems.Length >= 3)
                    MACAddr = devPathItems[devPathItems.Length - 3].ToUpper()                   // 1f882A25
                              + devPathItems[devPathItems.Length - 2].ToUpper()                 // 0
                              + devPathItems[devPathItems.Length - 1].TrimStart('0').ToUpper(); // 0001 -> 1
                else if (devPathItems.Length >= 1)
                    // Device and usb hub and port identifiers missing in devicePath string. Fallback to use vendor and product ID values and 
                    // take a number from the last part of the devicePath. Hopefully the last part is a usb port number as it usually should be.
                    MACAddr = this._deviceAttributes.VendorId.ToString("X4")
                              + this._deviceAttributes.ProductId.ToString("X4")
                              + devPathItems[devPathItems.Length - 1].TrimStart('0').ToUpper();

                if (!string.IsNullOrEmpty(MACAddr))
                {
                    MACAddr = MACAddr.PadRight(12, '0');
                    MACAddr = $"{MACAddr[0]}{MACAddr[1]}:{MACAddr[2]}{MACAddr[3]}:{MACAddr[4]}{MACAddr[5]}:{MACAddr[6]}{MACAddr[7]}:{MACAddr[8]}{MACAddr[9]}:{MACAddr[10]}{MACAddr[11]}";
                }
                else
                    // Hmm... Shold never come here. Strange format in devicePath because all identifier items of devicePath string are missing.
                    //serial = BLANK_SERIAL;
                    MACAddr = BLANK_SERIAL;
            }
            catch (Exception e)
            {
                AppLogger.LogToGui($"ERROR: Failed to generate runtime MAC address from device path {this.DevicePath}. {e.Message}", true);
                //serial = BLANK_SERIAL;
                MACAddr = BLANK_SERIAL;
            }

            return MACAddr;
        }
    }
}
