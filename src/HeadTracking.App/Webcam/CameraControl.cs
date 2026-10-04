using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace HeadTracking.App.Webcam
{
    /// <summary>
    /// Camera controls FlashCap does not expose, set through DirectShow's IAMCameraControl.
    ///
    /// The one that matters: UVC "auto-exposure priority" (KSPROPERTY_CAMERACONTROL_AUTO_EXPOSURE_PRIORITY,
    /// 19), which Logitech's software calls Low Light Compensation. While it is on, the camera may
    /// lengthen its exposure beyond one frame time, so in anything but bright light a "30 fps"
    /// camera delivers 15 frames a second at uneven intervals. Measured on a C920: 15.0 fps in every
    /// format, frame gaps alternating 64 and 80 ms. Off, auto exposure is held inside the frame time:
    /// a darker picture, but the full frame rate, which is what head tracking needs.
    /// </summary>
    public static class CameraControl
    {
        private const int AutoExposurePriority = 19;
        private const int FlagsAuto = 0x1;
        private const int FlagsManual = 0x2;

        private static readonly Guid SystemDeviceEnum = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        private static readonly Guid VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        private static readonly Guid IidBaseFilter = new Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770");
        private static readonly Guid IidPropertyBag = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");

        /// <summary>
        /// Sets auto-exposure priority on the named camera: 0 for full frame rate, 1 for the camera's
        /// brighter low-light behaviour. Returns a line for the log.
        /// </summary>
        public static string SetFrameRatePriority(string cameraName, bool keepFrameRate)
        {
            object filter = null;
            try
            {
                filter = FindFilter(cameraName);
                if (filter == null)
                {
                    return "Camera control: '" + cameraName + "' not found through DirectShow; frame rate left to the camera.";
                }

                if (!(filter is IAMCameraControl control))
                {
                    return "Camera control: " + cameraName + " has no camera controls; frame rate left to the camera.";
                }

                if (control.GetRange(AutoExposurePriority, out int min, out int max, out _, out int defaultValue, out _) != 0)
                {
                    return "Camera control: " + cameraName + " does not offer auto-exposure priority (low light compensation); nothing to change.";
                }

                control.Get(AutoExposurePriority, out int before, out _);
                // Not clamped to the reported range: a C920 reports 0..0 and still accepts 1.
                int wanted = keepFrameRate ? 0 : 1;
                int hr = control.Set(AutoExposurePriority, wanted, FlagsManual);
                control.Get(AutoExposurePriority, out int after, out _);
                return "Camera control: low light compensation (auto-exposure priority) was " + OnOff(before) + ", set " + OnOff(wanted)
                       + (hr == 0 ? "" : " FAILED (0x" + hr.ToString("X8") + ")") + ", now " + OnOff(after)
                       + " (range " + min + ".." + max + ", default " + defaultValue + ")"
                       + (keepFrameRate ? ": the camera keeps its full frame rate in low light; the picture is darker." : ": the camera may halve its frame rate in low light.");
            }
            catch (Exception e)
            {
                return "Camera control: could not set low light compensation on " + cameraName + ": " + e.Message;
            }
            finally
            {
                if (filter != null && Marshal.IsComObject(filter))
                {
                    Marshal.ReleaseComObject(filter);
                }
            }
        }

        private static string OnOff(int v) => v == 0 ? "off" : "on";

        private static object FindFilter(string cameraName)
        {
            Type type = Type.GetTypeFromCLSID(SystemDeviceEnum);
            ICreateDevEnum devEnum = (ICreateDevEnum)Activator.CreateInstance(type);
            try
            {
                Guid category = VideoInputDeviceCategory;
                if (devEnum.CreateClassEnumerator(ref category, out IEnumMoniker monikers, 0) != 0 || monikers == null)
                {
                    return null;
                }

                try
                {
                    IMoniker[] one = new IMoniker[1];
                    while (monikers.Next(1, one, IntPtr.Zero) == 0)
                    {
                        IMoniker moniker = one[0];
                        try
                        {
                            if (FriendlyName(moniker) == cameraName)
                            {
                                Guid iid = IidBaseFilter;
                                moniker.BindToObject(null, null, ref iid, out object filter);
                                return filter;
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(moniker);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(monikers);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(devEnum);
            }

            return null;
        }

        private static string FriendlyName(IMoniker moniker)
        {
            Guid iid = IidPropertyBag;
            moniker.BindToStorage(null, null, ref iid, out object bagObject);
            IPropertyBag bag = (IPropertyBag)bagObject;
            try
            {
                object value = null;
                return bag.Read("FriendlyName", ref value, IntPtr.Zero) == 0 ? value as string : null;
            }
            finally
            {
                Marshal.ReleaseComObject(bag);
            }
        }

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator([In] ref Guid category, [Out] out IEnumMoniker enumerator, [In] int flags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag
        {
            [PreserveSig]
            int Read([In, MarshalAs(UnmanagedType.LPWStr)] string name, [In, Out, MarshalAs(UnmanagedType.Struct)] ref object value, [In] IntPtr errorLog);

            [PreserveSig]
            int Write([In, MarshalAs(UnmanagedType.LPWStr)] string name, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
        }

        [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAMCameraControl
        {
            [PreserveSig]
            int GetRange([In] int property, [Out] out int min, [Out] out int max, [Out] out int steppingDelta, [Out] out int defaultValue, [Out] out int capsFlags);

            [PreserveSig]
            int Set([In] int property, [In] int value, [In] int flags);

            [PreserveSig]
            int Get([In] int property, [Out] out int value, [Out] out int flags);
        }
    }
}
