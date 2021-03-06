// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace System.Reflection.PortableExecutable
{
    /// <summary>
    /// An object used to read PE (Portable Executable) and COFF (Common Object File Format) headers from a stream.
    /// </summary>
    public sealed class PEHeaders
    {
        private readonly CoffHeader coffHeader;
        private readonly PEHeader peHeader;
        private readonly ImmutableArray<SectionHeader> sectionHeaders;
        private readonly CorHeader corHeader;
        private readonly int metadataStartOffset = -1;
        private readonly int metadataSize;
        private readonly int coffHeaderStartOffset = -1;
        private readonly int corHeaderStartOffset = -1;
        private readonly int peHeaderStartOffset = -1;

        /// <summary>
        /// Reads PE headers from the current location in the stream.
        /// </summary>
        /// <param name="peStream">Stream containing PE image starting at the stream's current position and ending at the end of the stream.</param>
        /// <exception cref="BadImageFormatException">The data read from stream have invalid format.</exception>
        /// <exception cref="IOException">Error reading from the stream.</exception>
        /// <exception cref="ArgumentException">The stream doesn't support seek operations.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="peStream"/> is null.</exception>
        public PEHeaders(Stream peStream)
           : this(peStream, null)
        {
        }

        /// <summary>
        /// Reads PE headers from the current location in the stream.
        /// </summary>
        /// <param name="peStream">Stream containing PE image of the given size starting at its current position.</param>
        /// <param name="size">Size of the PE image.</param>
        /// <exception cref="BadImageFormatException">The data read from stream have invalid format.</exception>
        /// <exception cref="IOException">Error reading from the stream.</exception>
        /// <exception cref="ArgumentException">The stream doesn't support seek operations.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="peStream"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Size is negative or extends past the end of the stream.</exception>
        public PEHeaders(Stream peStream, int size)
            : this(peStream, (int?)size)
        {
        }

        private PEHeaders(Stream peStream, int? sizeOpt)
        {
            if (peStream == null)
            {
                throw new ArgumentNullException("peStream");
            }

            if (!peStream.CanRead || !peStream.CanSeek)
            {
                throw new ArgumentException(MetadataResources.StreamMustSupportReadAndSeek, "peStream");
            }

            int size = PEBinaryReader.GetAndValidateSize(peStream, sizeOpt);
            var reader = new PEBinaryReader(peStream, size);

            bool isCoffOnly;
            SkipDosHeader(ref reader, out isCoffOnly);

            this.coffHeaderStartOffset = reader.CurrentOffset;
            this.coffHeader = new CoffHeader(ref reader);

            if (!isCoffOnly)
            {
                this.peHeaderStartOffset = reader.CurrentOffset;
                this.peHeader = new PEHeader(ref reader);
            }

            this.sectionHeaders = this.ReadSectionHeaders(ref reader);

            if (!isCoffOnly)
            {
                int offset;
                if (TryCalculateCorHeaderOffset(size, out offset))
                {
                    this.corHeaderStartOffset = offset;
                    reader.Seek(offset);
                    this.corHeader = new CorHeader(ref reader);
                }
            }

            CalculateMetadataLocation(size, out this.metadataStartOffset, out this.metadataSize);
        }

        /// <summary>
        /// Gets the offset (in bytes) from the start of the PE image to the start of the CLI metadata.
        /// or -1 if the image does not contain metadata.
        /// </summary>
        public int MetadataStartOffset
        {
            get { return metadataStartOffset; }
        }

        /// <summary>
        /// Gets the size of the CLI metadata 0 if the image does not contain metadata.)
        /// </summary>
        public int MetadataSize
        {
            get { return metadataSize; }
        }

        /// <summary>
        /// Gets the COFF header of the image.
        /// </summary>
        public CoffHeader CoffHeader
        {
            get { return coffHeader; }
        }

        /// <summary>
        /// Gets the byte offset from the start of the PE image to the start of the COFF header.
        /// </summary>
        public int CoffHeaderStartOffset
        {
            get { return coffHeaderStartOffset; }
        }

        /// <summary>
        /// Determines if the image is Coff only.
        /// </summary>
        public bool IsCoffOnly
        {
            get { return this.peHeader == null; }
        }

        /// <summary>
        /// Gets the PE header of the image or null if the image is COFF only.
        /// </summary>
        public PEHeader PEHeader
        {
            get { return peHeader; }
        }

        /// <summary>
        /// Gets the byte offset from the start of the image to 
        /// </summary>
        public int PEHeaderStartOffset
        {
            get { return peHeaderStartOffset; }
        }

        /// <summary>
        /// Gets the PE section headers.
        /// </summary>
        public ImmutableArray<SectionHeader> SectionHeaders
        {
            get { return sectionHeaders; }
        }

        /// <summary>
        /// Gets the CLI header or null if the image does not have one.
        /// </summary>
        public CorHeader CorHeader
        {
            get { return corHeader; }
        }

        /// <summary>
        /// Gets the byte offset from the start of the image to the COR header or -1 if the image does not have one.
        /// </summary>
        public int CorHeaderStartOffset
        {
            get { return corHeaderStartOffset; }
        }

        /// <summary>
        /// Determines if the image represents a Windows console application.
        /// </summary>
        public bool IsConsoleApplication
        {
            get
            {
                return peHeader != null && peHeader.Subsystem == Subsystem.WindowsCui;
            }
        }

        /// <summary>
        /// Determines if the image represents a dynamically linked library.
        /// </summary>
        public bool IsDll
        {
            get
            {
                return (coffHeader.Characteristics & Characteristics.Dll) != 0;
            }
        }

        /// <summary>
        /// Determines if the image represents an executable.
        /// </summary>
        public bool IsExe
        {
            get
            {
                return (coffHeader.Characteristics & Characteristics.Dll) == 0;
            }
        }

        private bool TryCalculateCorHeaderOffset(long peStreamSize, out int startOffset)
        {
            if (!TryGetDirectoryOffset(peHeader.CorHeaderTableDirectory, out startOffset))
            {
                startOffset = -1;
                return false;
            }

            int length = peHeader.CorHeaderTableDirectory.Size;
            if (length < COR20Constants.SizeOfCorHeader)
            {
                throw new BadImageFormatException(MetadataResources.InvalidCorHeaderSize);
            }

            return true;
        }

        private void SkipDosHeader(ref PEBinaryReader reader, out bool isCOFFOnly)
        {
            // Look for DOS Signature "MZ"
            ushort dosSig = reader.ReadUInt16();

            if (dosSig != PEFileConstants.DosSignature)
            {
                // If image doesn't start with DOS signature, let's assume it is a 
                // COFF (Common Object File Format), aka .OBJ file. 
                // See CLiteWeightStgdbRW::FindObjMetaData in ndp\clr\src\MD\enc\peparse.cpp

                if (dosSig != 0 || reader.ReadUInt16() != 0xffff)
                {
                    isCOFFOnly = true;
                    reader.Seek(0);
                }
                else
                {
                    // Might need to handle other formats. Anonymous or LTCG objects, for example.
                    throw new BadImageFormatException(MetadataResources.UnknownFileFormat);
                }
            }
            else
            {
                isCOFFOnly = false;
            }

            if (!isCOFFOnly)
            {
                // Skip the DOS Header
                reader.Seek(PEFileConstants.PESignatureOffsetLocation);

                int ntHeaderOffset = reader.ReadInt32();
                reader.Seek(ntHeaderOffset);

                // Look for PESignature "PE\0\0"
                uint ntSignature = reader.ReadUInt32();
                if (ntSignature != PEFileConstants.PESignature)
                {
                    throw new BadImageFormatException(MetadataResources.InvalidPESignature);
                }
            }
        }

        private ImmutableArray<SectionHeader> ReadSectionHeaders(ref PEBinaryReader reader)
        {
            int numberOfSections = this.coffHeader.NumberOfSections;
            if (numberOfSections < 0)
            {
                throw new BadImageFormatException(MetadataResources.InvalidNumberOfSections);
            }

            var builder = ImmutableArray.CreateBuilder<SectionHeader>(numberOfSections);

            for (int i = 0; i < numberOfSections; i++)
            {
                builder.Add(new SectionHeader(ref reader));
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// Gets the offset (in bytes) from the start of the image to the given directory entry.
        /// </summary>
        /// <param name="directory"></param>
        /// <param name="offset"></param>
        /// <returns>The section containing the directory could not be found.</returns>
        /// <exception cref="BadImageFormatException">The section containing the</exception>
        public bool TryGetDirectoryOffset(DirectoryEntry directory, out int offset)
        {
            int sectionIndex = GetContainingSectionIndex(directory.RelativeVirtualAddress);
            if (sectionIndex < 0)
            {
                offset = -1;
                return false;
            }

            int relativeOffset = directory.RelativeVirtualAddress - sectionHeaders[sectionIndex].VirtualAddress;
            if (directory.Size > sectionHeaders[sectionIndex].VirtualSize - relativeOffset)
            {
                throw new BadImageFormatException(MetadataResources.SectionTooSmall);
            }

            offset = sectionHeaders[sectionIndex].PointerToRawData + relativeOffset;
            return true;
        }

        /// <summary>
        /// Searches sections of the PE image for the one that contains specified Relative Virtual Address.
        /// </summary>
        /// <param name="relativeVirtualAddress">Address.</param>
        /// <returns>
        /// Index of the section that contains <paramref name="relativeVirtualAddress"/>,
        /// or -1 if there is none.
        /// </returns>
        public int GetContainingSectionIndex(int relativeVirtualAddress)
        {
            for (int i = 0; i < sectionHeaders.Length; i++)
            {
                if (sectionHeaders[i].VirtualAddress <= relativeVirtualAddress &&
                    relativeVirtualAddress < sectionHeaders[i].VirtualAddress + sectionHeaders[i].VirtualSize)
                {
                    return i;
                }
            }

            return -1;
        }

        private int IndexOfSection(string name)
        {
            for (int i = 0; i < SectionHeaders.Length; i++)
            {
                if (SectionHeaders[i].Name.Equals(name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private void CalculateMetadataLocation(long peImageSize, out int start, out int size)
        {
            if (IsCoffOnly)
            {
                int cormeta = IndexOfSection(".cormeta");
                if (cormeta == -1)
                {
                    start = -1;
                    size = 0;
                    return;
                }

                start = SectionHeaders[cormeta].PointerToRawData;
                size = SectionHeaders[cormeta].SizeOfRawData;
            }
            else if (corHeader == null)
            {
                start = 0;
                size = 0;
                return;
            }
            else
            {
                if (!TryGetDirectoryOffset(corHeader.MetadataDirectory, out start))
                {
                    throw new BadImageFormatException(MetadataResources.MissingDataDirectory);
                }

                size = corHeader.MetadataDirectory.Size;
            }

            if (start < 0 ||
                start >= peImageSize ||
                size <= 0 ||
                start > peImageSize - size)
            {
                throw new BadImageFormatException(MetadataResources.InvalidMetadataSectionSpan);
            }
        }
    }
}
