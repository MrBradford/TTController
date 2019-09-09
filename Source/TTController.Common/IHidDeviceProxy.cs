﻿using System;
using System.Collections.Generic;

namespace TTController.Common
{
    public interface IHidDeviceProxy : IDisposable
    {
        int VendorId { get; }
        int ProductId { get; }

        bool WriteBytes(params byte[] bytes);
        bool WriteBytes(IEnumerable<byte> bytes);
        byte[] ReadBytes();
        byte[] WriteReadBytes(params byte[] bytes);
        byte[] WriteReadBytes(IEnumerable<byte> bytes);
    }
}
