# Third-party notices

Head Tracking includes or is derived from the following.

## OpenTrack neuralnet tracker (code port and models)

The webcam tracker in `src/HeadTracking.App/Webcam` (`PoseMath.cs`, `HeadPoseNet.cs`,
`WebcamTracker.cs`, `ImageOps.cs`) is a C# port of OpenTrack's `tracker-neuralnet`, and the
release ships its ONNX models (`head-localizer.onnx`, `head-pose-0.4-*.onnx`).
Source: https://github.com/opentrack/opentrack/tree/master/tracker-neuralnet

> Copyright (c) 2021 Michael Welter <michael@welter-4d.de>
>
> Permission to use, copy, modify, and/or distribute this software for any
> purpose with or without fee is hereby granted, provided that the above
> copyright notice and this permission notice appear in all copies.

The OpenTrack UDP format and its loss behaviour were read from OpenTrack's `proto-udp` and
`logic/pipeline.cpp`:

> Copyright (c) 2012-2016 Stanislaw Halik. Copyright (c) 2015 Wim Vriend.
>
> Permission to use, copy, modify, and/or distribute this software for any
> purpose with or without fee is hereby granted, provided that the above
> copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.

## FlashCap

Camera capture. https://github.com/kekyo/FlashCap. Copyright (c) Kouji Matsui (@kozy_kekyo).
Licensed under the Apache License, Version 2.0: https://www.apache.org/licenses/LICENSE-2.0

## ONNX Runtime

Neural network inference. https://github.com/microsoft/onnxruntime. Copyright (c) Microsoft
Corporation. Licensed under the MIT License: https://github.com/microsoft/onnxruntime/blob/main/LICENSE

## .NET libraries

`System.Memory`, `System.Buffers`, `System.Numerics.Vectors` and
`System.Runtime.CompilerServices.Unsafe`, dependencies of ONNX Runtime's managed library.
Copyright (c) .NET Foundation and Contributors. Licensed under the MIT License.

## BepInEx and Harmony

The plugin runs under BepInEx (LGPL-2.1) and uses Harmony (MIT); neither is redistributed here.
