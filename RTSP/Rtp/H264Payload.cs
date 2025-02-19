﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rtsp.Onvif;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rtsp.Rtp
{
    // This class handles the H264 Payload
    // It has methods to parse parameters in the SDP
    // It has methods to process the RTP Payload

    public class H264Payload : IPayloadProcessor
    {
        private readonly ILogger _logger;

        int norm, fu_a, fu_b, stap_a, stap_b, mtap16, mtap24; // used for diagnostics stats

        // Stores the NAL units for a Video Frame. May be more than one NAL unit in a video frame.
        private readonly List<ReadOnlyMemory<byte>> nalUnits = [];
        private readonly List<IMemoryOwner<byte>> owners = [];
        // used to concatenate fragmented H264 NALs where NALs are split over RTP packets
        private readonly MemoryStream fragmentedNal = new();
        private readonly MemoryPool<byte> _memoryPool;

        private DateTime _timestamp;

        public H264Payload(ILogger<H264Payload>? logger, MemoryPool<byte>? memoryPool = null)
        {
            _logger = logger as ILogger ?? NullLogger.Instance;
            _memoryPool = memoryPool ?? MemoryPool<byte>.Shared;
        }

        public IList<ReadOnlyMemory<byte>> ProcessRTPPacket(RtpPacket packet, out DateTime? timestamp)
        {
            if (packet.Extension.Length > 0)
            {
                _timestamp = RtpPacketOnvifUtils.ProcessRTPTimestampExtension(packet.Extension, headerPosition: out _);
            }

            ProcessH264RTPFrame(packet.Payload);

            if (packet.IsMarker)
            {
                // Output some statistics
                _logger.LogDebug("Norm={norm} ST-A={stapA} ST-B={stapB} M16={mtap16} M24={mtap24} FU-A={fuA} FU-B={fuB}",
                    norm, stap_a, stap_b, mtap16, mtap24, fu_a, fu_b);

                // End Marker is set return the list of NALs
                var nalToReturn = nalUnits.ToList();
                nalUnits.Clear();
                owners.Clear();
                timestamp = _timestamp;
                return nalToReturn;
            }
            // we don't have a frame yet. Keep accumulating RTP packets
            timestamp = DateTime.MinValue;
            return [];
        }

        // Process a RTP Packet.
        // Returns a list of NAL Units (with no Size header)
        private void ProcessH264RTPFrame(ReadOnlySpan<byte> payload)
        {
            // Examine the first rtp_payload and the first byte (the NAL header)
            int nal_header_f_bit = payload[0] >> 7 & 0x01;
            int nal_header_nri = payload[0] >> 5 & 0x03;
            int nal_header_type = payload[0] >> 0 & 0x1F;

            // If the Nal Header Type is in the range 1..23 this is a normal NAL (not fragmented)
            // So write the NAL to the file
            if (nal_header_type >= 1 && nal_header_type <= 23)
            {
                _logger.LogDebug("Normal NAL");
                norm++;
                var nalSpan = PrepareNewNal(payload.Length);
                // copy the rest of the RTP payload to the memory stream
                payload.CopyTo(nalSpan);
            }
            // There are 4 types of Aggregation Packet (split over RTP payloads)
            else if (nal_header_type == 24)
            {
                _logger.LogDebug("Agg STAP-A");
                stap_a++;

                // RTP packet contains multiple NALs, each with a 16 bit header
                //   Read 16 byte size
                //   Read NAL
                try
                {
                    int ptr = 1; // start after the nal_header_type which was '24'
                                 // if we have at least 2 more bytes (the 16 bit size) then consume more data
                    while (ptr + 2 < payload.Length - 1)
                    {
                        int size = BinaryPrimitives.ReadUInt16BigEndian(payload[ptr..]);
                        ptr += 2;
                        var nalSpan = PrepareNewNal(size);
                        // copy the NAL
                        payload[ptr..(ptr + size)].CopyTo(nalSpan);
                        ptr += size;
                    }
                }
                catch (Exception err)
                {
                    _logger.LogWarning(err, "H264 Aggregate Packet processing error");
                }
            }
            else if (nal_header_type == 25)
            {
                _logger.LogWarning("Agg STAP-B not supported");
                stap_b++;
            }
            else if (nal_header_type == 26)
            {
                _logger.LogWarning("Agg MTAP16 not supported");
                mtap16++;
            }
            else if (nal_header_type == 27)
            {
                _logger.LogWarning("Agg MTAP24 not supported");
                mtap24++;
            }
            else if (nal_header_type == 28)
            {
                _logger.LogDebug("Frag FU-A");
                fu_a++;

                // Parse Fragmentation Unit Header
                int fu_header_s = payload[1] >> 7 & 0x01;  // start marker
                int fu_header_e = payload[1] >> 6 & 0x01;  // end marker
                                                           // int fu_header_r = payload[1] >> 5 & 0x01;  // reserved. should be 0
                int fu_header_type = payload[1] >> 0 & 0x1F; // Original NAL unit header

                _logger.LogDebug("Frag FU-A s={fuHeadersS} e={fuHeadersE}", fu_header_s, fu_header_e);

                // Check Start and End flags
                if (fu_header_s == 1 && fu_header_e == 0)
                {
                    // Start of Fragment.
                    // Initiise the fragmented_nal byte array
                    // Build the NAL header with the original F and NRI flags but use the the Type field from the fu_header_type
                    byte reconstructed_nal_type = (byte)((nal_header_f_bit << 7) + (nal_header_nri << 5) + fu_header_type);

                    // Empty the stream
                    fragmentedNal.SetLength(0);

                    // Add reconstructed_nal_type byte to the memory stream
                    fragmentedNal.WriteByte(reconstructed_nal_type);

                    // copy the rest of the RTP payload to the memory stream
                    fragmentedNal.Write(payload[2..]);
                }

                if (fu_header_s == 0 && fu_header_e == 0)
                {
                    // Middle part of Fragment
                    // Append this payload to the fragmented_nal
                    // Data starts after the NAL Unit Type byte and the FU Header byte
                    fragmentedNal.Write(payload[2..]);
                }

                if (fu_header_s == 0 && fu_header_e == 1)
                {
                    // End part of Fragment
                    // Append this payload to the fragmented_nal
                    // Data starts after the NAL Unit Type byte and the FU Header byte
                    fragmentedNal.Write(payload[2..]);

                    // Add the NAL to the array of NAL units
                    var length = (int)fragmentedNal.Length;
                    var nalSpan = PrepareNewNal(length);
                    fragmentedNal.GetBuffer().AsSpan()[..length].CopyTo(nalSpan);
                }
            }
            else if (nal_header_type == 29)
            {
                _logger.LogDebug("Frag FU-B not supported");
                fu_b++;
            }
            else
            {
                _logger.LogDebug("Unknown NAL header {nalHeaderType} not supported", nal_header_type);
            }
        }

        private Span<byte> PrepareNewNal(int sizeWitoutHeader)
        {
            var owner = _memoryPool.Rent(sizeWitoutHeader + 4);
            owners.Add(owner);
            var memory = owner.Memory[..(sizeWitoutHeader + 4)];
            nalUnits.Add(memory);
            // Add the NAL start code 00 00 00 01
            memory.Span[0] = 0;
            memory.Span[1] = 0;
            memory.Span[2] = 0;
            memory.Span[3] = 1;
            return memory[4..].Span;
        }

        public RawMediaFrame ProcessPacket(RtpPacket packet)
        {
            if (packet.Extension.Length > 0)
            {
                _timestamp = RtpPacketOnvifUtils.ProcessRTPTimestampExtension(packet.Extension, headerPosition: out _);
            }

            ProcessH264RTPFrame(packet.Payload);

            if (!packet.IsMarker)
            {
                // we don't have a frame yet. Keep accumulating RTP packets
                return RawMediaFrame.Empty;
            }

            // Output some statistics
            _logger.LogDebug("Norm={norm} ST-A={stapA} ST-B={stapB} M16={mtap16} M24={mtap24} FU-A={fuA} FU-B={fuB}",
                norm, stap_a, stap_b, mtap16, mtap24, fu_a, fu_b);

            // End Marker is set return the list of NALs
            // clone list of nalUnits and owners
            var result = new RawMediaFrame(
                new List<ReadOnlyMemory<byte>>(nalUnits),
                new List<IMemoryOwner<byte>>(owners)
                )
            {
                RtpTimestamp = packet.Timestamp,
                ClockTimestamp = _timestamp,
            };
            nalUnits.Clear();
            owners.Clear();
            return result;
        }
    }
}
