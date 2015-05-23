﻿// 
// Aurio: Audio Processing, Analysis and Retrieval Library
// Copyright (C) 2010-2015  Mario Guggenberger <mg@protyposis.net>
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Aurio.FFmpeg {
    /// <summary>
    /// Wraps the x86 and x64 interop functions and provides the correct ones depending on the execution platform.
    /// </summary>
    internal class InteropWrapper {

        // It would be cleaner/shorter to use Func<> pointers to save the delegate definitions, 
        // but they are not defined for out parameters
        // http://stackoverflow.com/a/20560385

        public delegate IntPtr d_stream_open(string filename);
        public delegate IntPtr d_stream_get_output_config(IntPtr instance);
        public delegate int d_stream_read_frame(IntPtr instance, out long timestamp, byte[] output_buffer, int output_buffer_size);
        public delegate void d_stream_seek(IntPtr instance, long timestamp);
        public delegate void d_stream_close(IntPtr instance);

        public static d_stream_open stream_open;
        public static d_stream_get_output_config stream_get_output_config;
        public static d_stream_read_frame stream_read_frame;
        public static d_stream_seek stream_seek;
        public static d_stream_close stream_close;

        static InteropWrapper() {
            if (Environment.Is64BitProcess) {
                stream_open = Interop64.stream_open;
                stream_get_output_config = Interop64.stream_get_output_config;
                stream_read_frame = Interop64.stream_read_frame;
                stream_seek = Interop64.stream_seek;
                stream_close = Interop64.stream_close;
            }
            else {
                stream_open = Interop32.stream_open;
                stream_get_output_config = Interop32.stream_get_output_config;
                stream_read_frame = Interop32.stream_read_frame;
                stream_seek = Interop32.stream_seek;
                stream_close = Interop32.stream_close;
            }
        }
    }
}