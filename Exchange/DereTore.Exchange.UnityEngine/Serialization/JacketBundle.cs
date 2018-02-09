using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DereTore.Common;
using DereTore.Exchange.UnityEngine.Extensions;

namespace DereTore.Exchange.UnityEngine.Serialization {
    public static class JacketBundle {

        public static void Serialize(BundleOptions options, Stream stream) {
            using (var writer = new EndianBinaryWriter(stream, UnityEndianHelper.UnityDefaultEndian)) {
                // bundle signature
                writer.WriteAsciiStringAndNull(BundleFileSignature.Raw);
                // bundle format
                writer.Write(3);
                writer.WriteAsciiStringAndNull(PlayerVersion);
                writer.WriteAsciiStringAndNull(EngineVersion);

                const int bundleHeaderSize = 0x70;
                const int assetHeaderSize = 0x10;
                int assetDataOffset;
                var singleAssetFile = GetAssetFileIndependentData(options, out assetDataOffset);
                assetDataOffset += bundleHeaderSize + assetHeaderSize;

                var totalBundleSize = bundleHeaderSize + assetHeaderSize + singleAssetFile.Length;
                writer.Write(totalBundleSize);
                writer.Write((ushort)0);
                // base offset; Raw file, no LZMA compression, so it is fixed.
                writer.Write((ushort)0x3c);
                // Dummy int
                writer.Write(1);
                // LZMA chunks
                writer.Write(1);
                // LZMA compressed size
                writer.Write(totalBundleSize - 0x3c);
                // LZMA stream size
                writer.Write(totalBundleSize - 0x3c);
                // some 'dummy' value (?)
                writer.Write(totalBundleSize);
                writer.AlignStream(4);
                // Dunno
                writer.Write(new byte[] { 0x00, 0x00, 0x34, 0x00 });
                // Now we should be at 0x3c

                // Asset file count
                writer.Write(1);
                // Asset file name
                var cabName = GetJacketBundleCabName(options.SongID);
                writer.WriteAsciiStringAndNull(cabName);
                // Asset file offset
                writer.Write(0x34);
                // first asset file offset = 0x70 = base offset + asset offset
                writer.Write(assetHeaderSize + singleAssetFile.Length);
                writer.AlignStream(4);
                // Now we should be at 0x70

                // Write asset header

                // table size
                writer.Write(0x08ba);
                // data end
                writer.Write(assetHeaderSize + singleAssetFile.Length);
                // format signature; 0xf for 5.1.2
                writer.Write(0xf);
                // data offset
                writer.Write(assetDataOffset - bundleHeaderSize);

                writer.Write(singleAssetFile);
            }
        }

        private static byte[] GetAssetFileIndependentData(BundleOptions options, out int dataStart) {
            using (var memoryStream = new MemoryStream()) {
                using (var writer = new EndianBinaryWriter(memoryStream, Endian.BigEndian)) {
                    // Still need:
                    // int32 table size
                    // int32 data end
                    // int32 format (=0xf)
                    // int32 data offset

                    // format signature (0xf = 5.x)
                    //writer.Write(0xf);
                    // 4 dummy bytes
                    writer.Write(0);
                    writer.WriteAsciiStringAndNull(EngineVersion);

                    // Switch to Little Endian from now on.
                    writer.Endian = Endian.LittleEndian;
                    // the 'platform' field indicates the endianess.
                    writer.Write(options.Platform);
                    // base definitions
                    writer.Write(true);

                    // base count
                    writer.Write(2);
                    // class ID
                    writer.Write(0x1c);
                    var classHash = new byte[] { 0x72, 0x30, 0xc9, 0xeb, 0x36, 0xa0, 0xc7, 0x2a, 0x71, 0x87, 0xad, 0x95, 0x25, 0x51, 0xe1, 0xe5 };
                    writer.Write(classHash);

                    // These structures seem to keep unchanged for a long time, so there's no need to recalculate them.
                    writer.Write(Texture2DClassStructure);
                    writer.Write(AssetBundleClassStructure);

                    var pvrFileName = $"jacket_{options.SongID:0000}_s";
                    var ddsFileName = $"jacket_{options.SongID:0000}_m";
                    var pvrData = WrapTexture2D(options.PvrImage, options.PvrWidth, options.PvrHeight, pvrFileName, TextureFormat.ETC_RGB4, writer.Endian);
                    var ddsData = WrapTexture2D(options.DdsImage, options.DdsWidth, options.DdsHeight, ddsFileName, TextureFormat.RGB565, writer.Endian);
                    dataStart = WritePreloadData(writer, pvrData, ddsData, options.SongID, options.DdsPathID, options.PvrPathID);

                    memoryStream.Capacity = (int)memoryStream.Length;
                    return memoryStream.ToArray();
                }
            }
        }

        /// <returns>Data start (from Preload Data Header + 0x10)</returns>
        private static int WritePreloadData(EndianBinaryWriter writer, byte[] pvrData, byte[] ddsData, int songID, long ddsID, long pvrID) {
            const int assetCount = 3;
            writer.Write(assetCount);

            //long pvrID = MathHelper.NextRandomInt64(), ddsID = MathHelper.NextRandomInt64();
            //long pvrID = unchecked((long)0xed362ad73c325b56), ddsID = 0x547e3042158b3095; // jacket_1001

            var assetData = new byte[assetCount][];
            assetData[0] = pvrData;
            assetData[1] = GetBundleIndexData(songID, pvrID, ddsID, writer.Endian);
            assetData[2] = ddsData;
            var preloadDataList = new List<AssetPreloadData>(assetCount);

            // PVR
            var pvrAssetData = new AssetPreloadData();
            preloadDataList.Add(pvrAssetData);
            pvrAssetData.PathID = pvrID;
            pvrAssetData.Size = assetData[0].Length;
            pvrAssetData.Type1 = pvrAssetData.Type2 = (ushort)PreloadDataType.Texture2D;

            // WTF (manifest?)
            var wtfAssetData = new AssetPreloadData();
            preloadDataList.Add(wtfAssetData);
            wtfAssetData.PathID = 1;
            wtfAssetData.Size = assetData[1].Length;
            wtfAssetData.Offset = MathHelper.RoundUpTo(pvrAssetData.Size, 8);
            // I don't know, this should have been AssetBundleManifest. But its value is definitely 142 (0x8e), which correspond to AssetBundle in original Unity Studio code.
            wtfAssetData.Type1 = wtfAssetData.Type2 = (ushort)PreloadDataType.AssetBundle;

            // DDS
            var ddsAssetData = new AssetPreloadData();
            preloadDataList.Add(ddsAssetData);
            ddsAssetData.PathID = ddsID;
            ddsAssetData.Size = assetData[2].Length;
            ddsAssetData.Offset = MathHelper.RoundUpTo(wtfAssetData.Offset + wtfAssetData.Size, 8);
            ddsAssetData.Type1 = ddsAssetData.Type2 = (ushort)PreloadDataType.Texture2D;

            for (var i = 0; i < assetCount; ++i) {
                var preloadData = preloadDataList[i];
                writer.Write(preloadData.PathID);
                writer.Write(preloadData.Offset);
                writer.Write(preloadData.Size);
                writer.Write(preloadData.Type1);
                writer.Write(preloadData.Type2);
                writer.Write((ushort)0xffff);
                // 'a lonely byte'
                writer.Write((byte)0);
                writer.AlignStream(4);
            }
            writer.AlignStream(16);
            // Ensure no fix-ups are needed and the Shared Assets Table is empty.
            writer.Write(new byte[0x730]);

            var dataStart = (int)writer.Position;
            for (var i = 0; i < assetCount; ++i) {
                writer.Write(assetData[i]);
                writer.AlignStream(8);
            }
            return dataStart;
        }

        /// <param name="format">Use ETC_RGB4 for PVR textures, and RGB565 for DDS textures.</param>
        private static byte[] WrapTexture2D(byte[] imageData, int width, int height, string name, TextureFormat format, Endian writerEndian) {
            using (var memoryStream = new MemoryStream()) {
                using (var writer = new EndianBinaryWriter(memoryStream, writerEndian)) {
                    writer.Write(name.Length);
                    writer.WriteAlignedUtf8String(name);
                    writer.Write(width);
                    writer.Write(height);
                    // complete image size
                    writer.Write(imageData.Length);
                    // texture format; However PVRTexTools reads it as ETC2 RGB while the original Unity Studio code marks it as ETC RGB4.
                    writer.Write((int)format);
                    // has mipmaps; Still writing false even when we have mipmaps.
                    writer.Write(false);
                    // is readable
                    writer.Write(false);
                    // is read allowed
                    writer.Write(true);
                    writer.AlignStream(4);

                    // image count
                    writer.Write(1);
                    // texture dimension
                    writer.Write(2);
                    writer.Write(FilterMode.Bilinear);
                    // aniso level
                    writer.Write(0);
                    // mip bias
                    writer.Write(0f);
                    writer.Write(WrapMode.Clamp);

                    // lightmap format
                    writer.Write(0);
                    // color space (lRGB)
                    writer.Write(1);

                    // image data size
                    writer.Write(imageData.Length);

                    writer.Write(imageData);

                    memoryStream.Capacity = (int)memoryStream.Length;
                    return memoryStream.ToArray();
                }
            }
        }

        private static string GetJacketBundleCabName(int songID) {
            var bundleFileName = $"jacket_{songID:0000}.unity3d";
            var cabName = GetStringMd4Hash(bundleFileName);
            return "CAB-" + cabName;
        }

        private static byte[] GetBundleIndexData(int songID, long pvrID, long ddsID, Endian writerEndian) {
            var bytes = Encoding.ASCII.GetBytes(songID.ToString("0000"));
            var bundleIndexData = (byte[])BundleIndexDataTemplate.Clone();
            var injectionIndices = new[] { 75, 87, 159, 171, 239 };
            foreach (var n in injectionIndices) {
                Array.Copy(bytes, 0, bundleIndexData, n, bytes.Length);
            }
            // The order of appearance of file is not important (see jacket_1001 and jacket_1009), but matching with correct names is.
            injectionIndices = new[] { 12, 196 };
            // Endian is not important either. But we must keep all the endians the same, in Preload Data and Bundle Index.
            // The easiest way is just calling BitConverter.GetBytes(), without any endian adjustments.
            bytes = UnityEndianHelper.GetBytes(pvrID, writerEndian);
            foreach (var n in injectionIndices) {
                Array.Copy(bytes, 0, bundleIndexData, n, bytes.Length);
            }
            injectionIndices = new[] { 24, 112 };
            bytes = UnityEndianHelper.GetBytes(ddsID, writerEndian);
            foreach (var n in injectionIndices) {
                Array.Copy(bytes, 0, bundleIndexData, n, bytes.Length);
            }
            return bundleIndexData;
        }

        private static string GenerateFakeHexHash(int hashStringLength) {
            if (hashStringLength % 2 != 0) {
                throw new ArgumentException("Expected hash string should have even number characters.", nameof(hashStringLength));
            }
            var bytes = new byte[hashStringLength / 2];
            MathHelper.Random.NextBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string GetStringMd4Hash(string str) {
            var bytes = Encoding.ASCII.GetBytes(str);
            var hashed = CabNameHash.Value.ComputeHash(bytes);
            return string.Join(string.Empty, hashed.Select(b => b.ToString("x2")));
        }

        private static readonly string PlayerVersion = "5.x.x";
        private static readonly string EngineVersion = "5.1.2f1";

        private static readonly Lazy<MD4> CabNameHash = new Lazy<MD4>(() => new MD4());

        private static readonly byte[] BundleIndexDataTemplate = {
            0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x56, 0x5b, 0x32, 0x3c,
            0xd7, 0x2a, 0x36, 0xed, 0x00, 0x00, 0x00, 0x00, 0x95, 0x30, 0x8b, 0x15, 0x42, 0x30, 0x7e, 0x54,
            0x02, 0x00, 0x00, 0x00, 0x39, 0x00, 0x00, 0x00, 0x61, 0x73, 0x73, 0x65, 0x74, 0x73, 0x2f, 0x5f,
            0x73, 0x74, 0x61, 0x67, 0x65, 0x77, 0x6f, 0x72, 0x6b, 0x2f, 0x72, 0x65, 0x73, 0x6f, 0x75, 0x72,
            0x63, 0x65, 0x73, 0x2f, 0x6a, 0x61, 0x63, 0x6b, 0x65, 0x74, 0x2f, 0x31, 0x30, 0x30, 0x31, 0x2f,
            0x6a, 0x61, 0x63, 0x6b, 0x65, 0x74, 0x5f, 0x31, 0x30, 0x30, 0x31, 0x5f, 0x6d, 0x2e, 0x70, 0x6e,
            0x67, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x95, 0x30, 0x8b, 0x15, 0x42, 0x30, 0x7e, 0x54, 0x39, 0x00, 0x00, 0x00, 0x61, 0x73, 0x73, 0x65,
            0x74, 0x73, 0x2f, 0x5f, 0x73, 0x74, 0x61, 0x67, 0x65, 0x77, 0x6f, 0x72, 0x6b, 0x2f, 0x72, 0x65,
            0x73, 0x6f, 0x75, 0x72, 0x63, 0x65, 0x73, 0x2f, 0x6a, 0x61, 0x63, 0x6b, 0x65, 0x74, 0x2f, 0x31,
            0x30, 0x30, 0x31, 0x2f, 0x6a, 0x61, 0x63, 0x6b, 0x65, 0x74, 0x5f, 0x31, 0x30, 0x30, 0x31, 0x5f,
            0x73, 0x2e, 0x70, 0x6e, 0x67, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x56, 0x5b, 0x32, 0x3c, 0xd7, 0x2a, 0x36, 0xed, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x6a, 0x61, 0x63, 0x6b, 0x65, 0x74, 0x5f, 0x31,
            0x30, 0x30, 0x31, 0x2e, 0x75, 0x6e, 0x69, 0x74, 0x79, 0x33, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };

        private static readonly byte[] Texture2DClassStructure = {
            0x18, 0x00, 0x00, 0x00, 0xf0, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x6a, 0x03, 0x00, 0x80,
            0x37, 0x00, 0x00, 0x80, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0x48, 0x03, 0x00, 0x80, 0xab, 0x01, 0x00, 0x80, 0xff, 0xff, 0xff, 0xff,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x80, 0x00, 0x00, 0x01, 0x00, 0x02, 0x01, 0x31, 0x00, 0x00, 0x80,
            0x31, 0x00, 0x00, 0x80, 0xff, 0xff, 0xff, 0xff, 0x02, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00,
            0x01, 0x00, 0x03, 0x00, 0xde, 0x00, 0x00, 0x80, 0x1b, 0x03, 0x00, 0x80, 0x04, 0x00, 0x00, 0x00,
            0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0x51, 0x00, 0x00, 0x80,
            0x6a, 0x00, 0x00, 0x80, 0x01, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0xde, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
            0x05, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xde, 0x00, 0x00, 0x80,
            0x08, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0xde, 0x00, 0x00, 0x80, 0x11, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
            0x07, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xde, 0x00, 0x00, 0x80,
            0x25, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0x4c, 0x00, 0x00, 0x80, 0x35, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x09, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x4c, 0x00, 0x00, 0x80,
            0x3e, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0x4c, 0x00, 0x00, 0x80, 0x4b, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x0b, 0x00, 0x00, 0x00, 0x10, 0x40, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xde, 0x00, 0x00, 0x80,
            0x59, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x0c, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0xde, 0x00, 0x00, 0x80, 0x66, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
            0x0d, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x79, 0x00, 0x00, 0x00,
            0x8b, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x0e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x02, 0x00, 0xde, 0x00, 0x00, 0x80, 0x9d, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
            0x0f, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0xde, 0x00, 0x00, 0x80,
            0xaa, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x02, 0x00, 0xa1, 0x00, 0x00, 0x80, 0xb2, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
            0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0xde, 0x00, 0x00, 0x80,
            0xbc, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0xde, 0x00, 0x00, 0x80, 0xc7, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
            0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xde, 0x00, 0x00, 0x80,
            0xd8, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x01, 0x7e, 0x03, 0x00, 0x80, 0xe5, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff,
            0x15, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0xde, 0x00, 0x00, 0x80,
            0x1b, 0x03, 0x00, 0x80, 0x04, 0x00, 0x00, 0x00, 0x16, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x02, 0x00, 0xa0, 0x03, 0x00, 0x80, 0x6a, 0x00, 0x00, 0x80, 0x01, 0x00, 0x00, 0x00,
            0x17, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x6d, 0x5f, 0x57, 0x69, 0x64, 0x74, 0x68, 0x00,
            0x6d, 0x5f, 0x48, 0x65, 0x69, 0x67, 0x68, 0x74, 0x00, 0x6d, 0x5f, 0x43, 0x6f, 0x6d, 0x70, 0x6c,
            0x65, 0x74, 0x65, 0x49, 0x6d, 0x61, 0x67, 0x65, 0x53, 0x69, 0x7a, 0x65, 0x00, 0x6d, 0x5f, 0x54,
            0x65, 0x78, 0x74, 0x75, 0x72, 0x65, 0x46, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x00, 0x6d, 0x5f, 0x4d,
            0x69, 0x70, 0x4d, 0x61, 0x70, 0x00, 0x6d, 0x5f, 0x49, 0x73, 0x52, 0x65, 0x61, 0x64, 0x61, 0x62,
            0x6c, 0x65, 0x00, 0x6d, 0x5f, 0x52, 0x65, 0x61, 0x64, 0x41, 0x6c, 0x6c, 0x6f, 0x77, 0x65, 0x64,
            0x00, 0x6d, 0x5f, 0x49, 0x6d, 0x61, 0x67, 0x65, 0x43, 0x6f, 0x75, 0x6e, 0x74, 0x00, 0x6d, 0x5f,
            0x54, 0x65, 0x78, 0x74, 0x75, 0x72, 0x65, 0x44, 0x69, 0x6d, 0x65, 0x6e, 0x73, 0x69, 0x6f, 0x6e,
            0x00, 0x47, 0x4c, 0x54, 0x65, 0x78, 0x74, 0x75, 0x72, 0x65, 0x53, 0x65, 0x74, 0x74, 0x69, 0x6e,
            0x67, 0x73, 0x00, 0x6d, 0x5f, 0x54, 0x65, 0x78, 0x74, 0x75, 0x72, 0x65, 0x53, 0x65, 0x74, 0x74,
            0x69, 0x6e, 0x67, 0x73, 0x00, 0x6d, 0x5f, 0x46, 0x69, 0x6c, 0x74, 0x65, 0x72, 0x4d, 0x6f, 0x64,
            0x65, 0x00, 0x6d, 0x5f, 0x41, 0x6e, 0x69, 0x73, 0x6f, 0x00, 0x6d, 0x5f, 0x4d, 0x69, 0x70, 0x42,
            0x69, 0x61, 0x73, 0x00, 0x6d, 0x5f, 0x57, 0x72, 0x61, 0x70, 0x4d, 0x6f, 0x64, 0x65, 0x00, 0x6d,
            0x5f, 0x4c, 0x69, 0x67, 0x68, 0x74, 0x6d, 0x61, 0x70, 0x46, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x00,
            0x6d, 0x5f, 0x43, 0x6f, 0x6c, 0x6f, 0x72, 0x53, 0x70, 0x61, 0x63, 0x65, 0x00, 0x69, 0x6d, 0x61,
            0x67, 0x65, 0x20, 0x64, 0x61, 0x74, 0x61, 0x00
        };

        private static readonly byte[] AssetBundleClassStructure =  {
            0x8e, 0x00, 0x00, 0x00, 0xf2, 0x84, 0xab, 0xb6, 0xf2, 0xdf, 0x17, 0xeb, 0xed, 0xae, 0x2b, 0x6b,
            0x50, 0x43, 0x68, 0x10, 0x2c, 0x00, 0x00, 0x00, 0xc3, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x37, 0x00, 0x00, 0x80, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x48, 0x03, 0x00, 0x80, 0xab, 0x01, 0x00, 0x80,
            0xff, 0xff, 0xff, 0xff, 0x01, 0x00, 0x00, 0x00, 0x01, 0x80, 0x00, 0x00, 0x01, 0x00, 0x02, 0x01,
            0x31, 0x00, 0x00, 0x80, 0x31, 0x00, 0x00, 0x80, 0xff, 0xff, 0xff, 0xff, 0x02, 0x00, 0x00, 0x00,
            0x01, 0x40, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0xde, 0x00, 0x00, 0x80, 0x1b, 0x03, 0x00, 0x80,
            0x04, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00,
            0x51, 0x00, 0x00, 0x80, 0x6a, 0x00, 0x00, 0x80, 0x01, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xd5, 0x03, 0x00, 0x80, 0x0c, 0x00, 0x00, 0x00,
            0xff, 0xff, 0xff, 0xff, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x01,
            0x31, 0x00, 0x00, 0x80, 0x31, 0x00, 0x00, 0x80, 0xff, 0xff, 0xff, 0xff, 0x06, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0xde, 0x00, 0x00, 0x80, 0x1b, 0x03, 0x00, 0x80,
            0x04, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00,
            0x79, 0x02, 0x00, 0x80, 0x6a, 0x00, 0x00, 0x80, 0x0c, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x04, 0x00, 0xde, 0x00, 0x00, 0x80, 0x1b, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x04, 0x00,
            0x2e, 0x03, 0x00, 0x80, 0x24, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xf1, 0x00, 0x00, 0x80, 0x2d, 0x00, 0x00, 0x00,
            0xff, 0xff, 0xff, 0xff, 0x0b, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x02, 0x01,
            0x31, 0x00, 0x00, 0x80, 0x31, 0x00, 0x00, 0x80, 0xff, 0xff, 0xff, 0xff, 0x0c, 0x00, 0x00, 0x00,
            0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0xde, 0x00, 0x00, 0x80, 0x1b, 0x03, 0x00, 0x80,
            0x04, 0x00, 0x00, 0x00, 0x0d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00,
            0x1f, 0x02, 0x00, 0x80, 0x6a, 0x00, 0x00, 0x80, 0xff, 0xff, 0xff, 0xff, 0x0e, 0x00, 0x00, 0x00,
            0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x04, 0x00, 0x48, 0x03, 0x00, 0x80, 0x9b, 0x00, 0x00, 0x80,
            0xff, 0xff, 0xff, 0xff, 0x0f, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x05, 0x01,
            0x31, 0x00, 0x00, 0x80, 0x31, 0x00, 0x00, 0x80, 0xff, 0xff, 0xff, 0xff, 0x10, 0x00, 0x00, 0x00,
            0x01, 0x40, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0xde, 0x00, 0x00, 0x80, 0x1b, 0x03, 0x00, 0x80,
            0x04, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00,
            0x51, 0x00, 0x00, 0x80, 0x6a, 0x00, 0x00, 0x80, 0x01, 0x00, 0x00, 0x00, 0x12, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x04, 0x00, 0x39, 0x00, 0x00, 0x00, 0x0a, 0x03, 0x00, 0x80,
            0x14, 0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x05, 0x00,
            0xde, 0x00, 0x00, 0x80, 0x43, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x05, 0x00, 0xde, 0x00, 0x00, 0x80, 0x50, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x05, 0x00,
            0x79, 0x02, 0x00, 0x80, 0x5c, 0x00, 0x00, 0x00, 0x0c, 0x00, 0x00, 0x00, 0x16, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0xde, 0x00, 0x00, 0x80, 0x1b, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x17, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00,
            0x2e, 0x03, 0x00, 0x80, 0x24, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x39, 0x00, 0x00, 0x00, 0x62, 0x00, 0x00, 0x00,
            0x14, 0x00, 0x00, 0x00, 0x19, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00,
            0xde, 0x00, 0x00, 0x80, 0x43, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x1a, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0xde, 0x00, 0x00, 0x80, 0x50, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x1b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00,
            0x79, 0x02, 0x00, 0x80, 0x5c, 0x00, 0x00, 0x00, 0x0c, 0x00, 0x00, 0x00, 0x1c, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0xde, 0x00, 0x00, 0x80, 0x1b, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x1d, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00,
            0x2e, 0x03, 0x00, 0x80, 0x24, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x1e, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xa6, 0x03, 0x00, 0x80, 0x6e, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x1f, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
            0x48, 0x03, 0x00, 0x80, 0x85, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0x20, 0x00, 0x00, 0x00,
            0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x02, 0x01, 0x31, 0x00, 0x00, 0x80, 0x31, 0x00, 0x00, 0x80,
            0xff, 0xff, 0xff, 0xff, 0x21, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00,
            0xde, 0x00, 0x00, 0x80, 0x1b, 0x03, 0x00, 0x80, 0x04, 0x00, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0x51, 0x00, 0x00, 0x80, 0x6a, 0x00, 0x00, 0x80,
            0x01, 0x00, 0x00, 0x00, 0x23, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
            0xd5, 0x03, 0x00, 0x80, 0x97, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0x24, 0x00, 0x00, 0x00,
            0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x02, 0x01, 0x31, 0x00, 0x00, 0x80, 0x31, 0x00, 0x00, 0x80,
            0xff, 0xff, 0xff, 0xff, 0x25, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00,
            0xde, 0x00, 0x00, 0x80, 0x1b, 0x03, 0x00, 0x80, 0x04, 0x00, 0x00, 0x00, 0x26, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0x48, 0x03, 0x00, 0x80, 0x6a, 0x00, 0x00, 0x80,
            0xff, 0xff, 0xff, 0xff, 0x27, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x04, 0x01,
            0x31, 0x00, 0x00, 0x80, 0x31, 0x00, 0x00, 0x80, 0xff, 0xff, 0xff, 0xff, 0x28, 0x00, 0x00, 0x00,
            0x01, 0x40, 0x00, 0x00, 0x01, 0x00, 0x05, 0x00, 0xde, 0x00, 0x00, 0x80, 0x1b, 0x03, 0x00, 0x80,
            0x04, 0x00, 0x00, 0x00, 0x29, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x05, 0x00,
            0x51, 0x00, 0x00, 0x80, 0x6a, 0x00, 0x00, 0x80, 0x01, 0x00, 0x00, 0x00, 0x2a, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x4c, 0x00, 0x00, 0x80, 0xa6, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x2b, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x41, 0x73, 0x73, 0x65,
            0x74, 0x42, 0x75, 0x6e, 0x64, 0x6c, 0x65, 0x00, 0x6d, 0x5f, 0x50, 0x72, 0x65, 0x6c, 0x6f, 0x61,
            0x64, 0x54, 0x61, 0x62, 0x6c, 0x65, 0x00, 0x6d, 0x5f, 0x46, 0x69, 0x6c, 0x65, 0x49, 0x44, 0x00,
            0x6d, 0x5f, 0x50, 0x61, 0x74, 0x68, 0x49, 0x44, 0x00, 0x6d, 0x5f, 0x43, 0x6f, 0x6e, 0x74, 0x61,
            0x69, 0x6e, 0x65, 0x72, 0x00, 0x41, 0x73, 0x73, 0x65, 0x74, 0x49, 0x6e, 0x66, 0x6f, 0x00, 0x70,
            0x72, 0x65, 0x6c, 0x6f, 0x61, 0x64, 0x49, 0x6e, 0x64, 0x65, 0x78, 0x00, 0x70, 0x72, 0x65, 0x6c,
            0x6f, 0x61, 0x64, 0x53, 0x69, 0x7a, 0x65, 0x00, 0x61, 0x73, 0x73, 0x65, 0x74, 0x00, 0x6d, 0x5f,
            0x4d, 0x61, 0x69, 0x6e, 0x41, 0x73, 0x73, 0x65, 0x74, 0x00, 0x6d, 0x5f, 0x52, 0x75, 0x6e, 0x74,
            0x69, 0x6d, 0x65, 0x43, 0x6f, 0x6d, 0x70, 0x61, 0x74, 0x69, 0x62, 0x69, 0x6c, 0x69, 0x74, 0x79,
            0x00, 0x6d, 0x5f, 0x41, 0x73, 0x73, 0x65, 0x74, 0x42, 0x75, 0x6e, 0x64, 0x6c, 0x65, 0x4e, 0x61,
            0x6d, 0x65, 0x00, 0x6d, 0x5f, 0x44, 0x65, 0x70, 0x65, 0x6e, 0x64, 0x65, 0x6e, 0x63, 0x69, 0x65,
            0x73, 0x00, 0x6d, 0x5f, 0x49, 0x73, 0x53, 0x74, 0x72, 0x65, 0x61, 0x6d, 0x65, 0x64, 0x53, 0x63,
            0x65, 0x6e, 0x65, 0x41, 0x73, 0x73, 0x65, 0x74, 0x42, 0x75, 0x6e, 0x64, 0x6c, 0x65, 0x00
        };

    }
}
