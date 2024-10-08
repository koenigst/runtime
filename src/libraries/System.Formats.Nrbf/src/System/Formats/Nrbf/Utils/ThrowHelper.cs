﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Runtime.Serialization;

namespace System.Formats.Nrbf.Utils;

// The exception messages do not contain member/type/assembly names on purpose,
// as it's most likely corrupted/tampered/malicious data.
internal static class ThrowHelper
{
    internal static void ThrowDuplicateMemberName()
        => throw new SerializationException(SR.Serialization_DuplicateMemberName);

    internal static void ThrowInvalidValue(int value)
        => throw new SerializationException(SR.Format(SR.Serialization_InvalidValue, value));

    internal static void ThrowInvalidReference()
        => throw new SerializationException(SR.Serialization_InvalidReference);

    internal static void ThrowInvalidTypeName()
        => throw new SerializationException(SR.Serialization_InvalidTypeName);

    internal static void ThrowUnexpectedNullRecordCount()
        => throw new SerializationException(SR.Serialization_UnexpectedNullRecordCount);

    internal static void ThrowArrayContainedNulls()
        => throw new SerializationException(SR.Serialization_ArrayContainedNulls);

    internal static void ThrowInvalidAssemblyName()
        => throw new SerializationException(SR.Serialization_InvalidAssemblyName);

    internal static void ThrowInvalidFormat()
        => throw new SerializationException(SR.Serialization_InvalidFormat);

    internal static void ThrowEndOfStreamException()
        => throw new EndOfStreamException();

    internal static void ThrowForUnexpectedRecordType(byte recordType)
    {
        // The enum values are not part of the public API surface, as they are not supported
        // and users don't need to handle these values.

#pragma warning disable IDE0066 // Convert switch statement to expression
        switch (recordType)
        {
            case (byte)SerializationRecordType.SystemClassWithMembers: // generated without FormatterTypeStyle.TypesAlways
            case (byte)SerializationRecordType.ClassWithMembers: // generated without FormatterTypeStyle.TypesAlways
            // 18~20 are from the reference source but aren't in the OpenSpecs doc
            case 18: // CrossAppDomainMap
            case 19: // CrossAppDomainString
            case 20: // CrossAppDomainAssembly
            case (byte)SerializationRecordType.MethodCall:
            case (byte)SerializationRecordType.MethodReturn:
                throw new NotSupportedException(SR.Format(SR.NotSupported_RecordType, recordType));
            default:
                ThrowInvalidValue(recordType);
                break;
        }
#pragma warning restore IDE0066 // Convert switch statement to expression
    }
}
