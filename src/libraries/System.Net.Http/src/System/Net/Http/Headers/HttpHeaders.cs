// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Net.Http.Headers
{
    /// <summary>
    /// Key/value pairs of headers. The value is either a raw <see cref="string"/> or a <see cref="HttpHeaders.HeaderStoreItemInfo"/>.
    /// We're using a custom type instead of <see cref="KeyValuePair{TKey, TValue}"/> because we need ref access to fields.
    /// </summary>
    internal struct HeaderEntry
    {
        public HeaderDescriptor Key;
        public object Value;

        public HeaderEntry(HeaderDescriptor key, object value)
        {
            Key = key;
            Value = value;
        }
    }

    public abstract class HttpHeaders : IEnumerable<KeyValuePair<string, IEnumerable<string>>>
    {
        // This type is used to store a collection of headers in 'headerStore':
        // - A header can have multiple values.
        // - A header can have an associated parser which is able to parse the raw string value into a strongly typed object.
        // - If a header has an associated parser and the provided raw value can't be parsed, the value is considered
        //   invalid. Invalid values are stored if added using TryAddWithoutValidation(). If the value was added using Add(),
        //   Add() will throw FormatException.
        // - Since parsing header values is expensive and users usually only care about a few headers, header values are
        //   lazily initialized.
        //
        // Given the properties above, a header value can have three states:
        // - 'raw': The header value was added using TryAddWithoutValidation() and it wasn't parsed yet.
        // - 'parsed': The header value was successfully parsed. It was either added using Add() where the value was parsed
        //   immediately, or if added using TryAddWithoutValidation() a user already accessed a property/method triggering the
        //   value to be parsed.
        // - 'invalid': The header value was parsed, but parsing failed because the value is invalid. Storing invalid values
        //   allows users to still retrieve the value (by calling GetValues()), but it will not be exposed as strongly typed
        //   object. E.g. the client receives a response with the following header: 'Via: 1.1 proxy, invalid'
        //   - HttpHeaders.GetValues() will return "1.1 proxy", "invalid"
        //   - HttpResponseHeaders.Via collection will only contain one ViaHeaderValue object with value "1.1 proxy"

        /// <summary>Either a <see cref="HeaderEntry"/> array or a Dictionary&lt;<see cref="HeaderDescriptor"/>, <see cref="object"/>&gt; </summary>
        private object? _headerStore;
        private int _count;

        private readonly HttpHeaderType _allowedHeaderTypes;
        private readonly HttpHeaderType _treatAsCustomHeaderTypes;

        protected HttpHeaders()
            : this(HttpHeaderType.All, HttpHeaderType.None)
        {
        }

        internal HttpHeaders(HttpHeaderType allowedHeaderTypes, HttpHeaderType treatAsCustomHeaderTypes)
        {
            // Should be no overlap
            Debug.Assert((allowedHeaderTypes & treatAsCustomHeaderTypes) == 0);

            _allowedHeaderTypes = allowedHeaderTypes & ~HttpHeaderType.NonTrailing;
            _treatAsCustomHeaderTypes = treatAsCustomHeaderTypes & ~HttpHeaderType.NonTrailing;
        }

        /// <summary>Gets a view of the contents of this headers collection that does not parse nor validate the data upon access.</summary>
        public HttpHeadersNonValidated NonValidated => new HttpHeadersNonValidated(this);

        public void Add(string name, string? value) => Add(GetHeaderDescriptor(name), value);

        internal void Add(HeaderDescriptor descriptor, string? value)
        {
            // We don't use GetOrCreateHeaderInfo() here, since this would create a new header in the store. If parsing
            // the value then throws, we would have to remove the header from the store again. So just get a
            // HeaderStoreItemInfo object and try to parse the value. If it works, we'll add the header.
            PrepareHeaderInfoForAdd(descriptor, out HeaderStoreItemInfo info, out bool addToStore);
            ParseAndAddValue(descriptor, info, value);

            // If we get here, then the value could be parsed correctly. If we created a new HeaderStoreItemInfo, add
            // it to the store if we added at least one value.
            if (addToStore && (info.ParsedValue != null))
            {
                Debug.Assert(!ContainsKey(descriptor));
                AddEntryToStore(new HeaderEntry(descriptor, info));
            }
        }

        public void Add(string name, IEnumerable<string?> values) => Add(GetHeaderDescriptor(name), values);

        internal void Add(HeaderDescriptor descriptor, IEnumerable<string?> values!!)
        {
            PrepareHeaderInfoForAdd(descriptor, out HeaderStoreItemInfo info, out bool addToStore);

            try
            {
                // Note that if the first couple of values are valid followed by an invalid value, the valid values
                // will be added to the store before the exception for the invalid value is thrown.
                foreach (string? value in values)
                {
                    ParseAndAddValue(descriptor, info, value);
                }
            }
            finally
            {
                // Even if one of the values was invalid, make sure we add the header for the valid ones. We need to be
                // consistent here: If values get added to an _existing_ header, then all values until the invalid one
                // get added. Same here: If multiple values get added to a _new_ header, make sure the header gets added
                // with the valid values.
                // However, if all values for a _new_ header were invalid, then don't add the header.
                if (addToStore && (info.ParsedValue != null))
                {
                    Debug.Assert(!ContainsKey(descriptor));
                    AddEntryToStore(new HeaderEntry(descriptor, info));
                }
            }
        }

        public bool TryAddWithoutValidation(string name, string? value) =>
            TryGetHeaderDescriptor(name, out HeaderDescriptor descriptor) &&
            TryAddWithoutValidation(descriptor, value);

        internal bool TryAddWithoutValidation(HeaderDescriptor descriptor, string? value)
        {
            // Normalize null values to be empty values, which are allowed. If the user adds multiple
            // null/empty values, all of them are added to the collection. This will result in delimiter-only
            // values, e.g. adding two null-strings (or empty, or whitespace-only) results in "My-Header: ,".
            value ??= string.Empty;

            ref object? storeValueRef = ref GetValueRefOrAddDefault(descriptor);
            object? currentValue = storeValueRef;

            if (currentValue is null)
            {
                storeValueRef = value;
            }
            else
            {
                if (currentValue is not HeaderStoreItemInfo info)
                {
                    // The header store contained a single raw string value, so promote it
                    // to being a HeaderStoreItemInfo and add to it.
                    Debug.Assert(currentValue is string);
                    storeValueRef = info = new HeaderStoreItemInfo() { RawValue = currentValue };
                }

                AddRawValue(info, value);
            }

            return true;
        }

        public bool TryAddWithoutValidation(string name, IEnumerable<string?> values) =>
            TryGetHeaderDescriptor(name, out HeaderDescriptor descriptor) &&
            TryAddWithoutValidation(descriptor, values);

        internal bool TryAddWithoutValidation(HeaderDescriptor descriptor, IEnumerable<string?> values!!)
        {
            using IEnumerator<string?> enumerator = values.GetEnumerator();
            if (enumerator.MoveNext())
            {
                TryAddWithoutValidation(descriptor, enumerator.Current);
                if (enumerator.MoveNext())
                {
                    ref object? storeValueRef = ref GetValueRefOrAddDefault(descriptor);
                    Debug.Assert(storeValueRef is not null);

                    object value = storeValueRef;
                    if (value is not HeaderStoreItemInfo info)
                    {
                        Debug.Assert(value is string);
                        storeValueRef = info = new HeaderStoreItemInfo { RawValue = value };
                    }

                    do
                    {
                        AddRawValue(info, enumerator.Current ?? string.Empty);
                    }
                    while (enumerator.MoveNext());
                }
            }

            return true;
        }

        public IEnumerable<string> GetValues(string name) => GetValues(GetHeaderDescriptor(name));

        internal IEnumerable<string> GetValues(HeaderDescriptor descriptor)
        {
            if (TryGetValues(descriptor, out IEnumerable<string>? values))
            {
                return values;
            }

            throw new InvalidOperationException(SR.net_http_headers_not_found);
        }

        public bool TryGetValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            if (TryGetHeaderDescriptor(name, out HeaderDescriptor descriptor))
            {
                return TryGetValues(descriptor, out values);
            }

            values = null;
            return false;
        }

        internal bool TryGetValues(HeaderDescriptor descriptor, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            if (TryGetAndParseHeaderInfo(descriptor, out HeaderStoreItemInfo? info))
            {
                values = GetStoreValuesAsStringArray(descriptor, info);
                return true;
            }

            values = null;
            return false;
        }

        public bool Contains(string name) => Contains(GetHeaderDescriptor(name));

        internal bool Contains(HeaderDescriptor descriptor)
        {
            // We can't just call headerStore.ContainsKey() since after parsing the value the header may not exist
            // anymore (if the value contains newline chars, we remove the header). So try to parse the
            // header value.
            return TryGetAndParseHeaderInfo(descriptor, out _);
        }

        public override string ToString()
        {
            // Return all headers as string similar to:
            // HeaderName1: Value1, Value2
            // HeaderName2: Value1
            // ...

            var vsb = new ValueStringBuilder(stackalloc char[512]);

            foreach (HeaderEntry entry in GetEntries())
            {
                vsb.Append(entry.Key.Name);
                vsb.Append(": ");

                GetStoreValuesAsStringOrStringArray(entry.Key, entry.Value, out string? singleValue, out string[]? multiValue);
                Debug.Assert(singleValue is not null ^ multiValue is not null);

                if (singleValue is not null)
                {
                    vsb.Append(singleValue);
                }
                else
                {
                    // Note that if we get multiple values for a header that doesn't support multiple values, we'll
                    // just separate the values using a comma (default separator).
                    string? separator = entry.Key.Parser is HttpHeaderParser parser && parser.SupportsMultipleValues ? parser.Separator : HttpHeaderParser.DefaultSeparator;

                    Debug.Assert(multiValue is not null && multiValue.Length > 0);
                    vsb.Append(multiValue[0]);
                    for (int i = 1; i < multiValue.Length; i++)
                    {
                        vsb.Append(separator);
                        vsb.Append(multiValue[i]);
                    }
                }

                vsb.Append(Environment.NewLine);
            }

            return vsb.ToString();
        }

        internal string GetHeaderString(HeaderDescriptor descriptor)
        {
            if (TryGetHeaderValue(descriptor, out object? info))
            {
                GetStoreValuesAsStringOrStringArray(descriptor, info, out string? singleValue, out string[]? multiValue);
                Debug.Assert(singleValue is not null ^ multiValue is not null);

                if (singleValue is not null)
                {
                    return singleValue;
                }

                // Note that if we get multiple values for a header that doesn't support multiple values, we'll
                // just separate the values using a comma (default separator).
                string? separator = descriptor.Parser != null && descriptor.Parser.SupportsMultipleValues ? descriptor.Parser.Separator : HttpHeaderParser.DefaultSeparator;
                return string.Join(separator, multiValue!);
            }

            return string.Empty;
        }

        #region IEnumerable<KeyValuePair<string, IEnumerable<string>>> Members

        public IEnumerator<KeyValuePair<string, IEnumerable<string>>> GetEnumerator() => _count == 0 ?
                ((IEnumerable<KeyValuePair<string, IEnumerable<string>>>)Array.Empty<KeyValuePair<string, IEnumerable<string>>>()).GetEnumerator() :
                GetEnumeratorCore();

        private IEnumerator<KeyValuePair<string, IEnumerable<string>>> GetEnumeratorCore()
        {
            HeaderEntry[]? entries = GetEntriesArray();
            Debug.Assert(_count != 0 && entries is not null, "Caller should have validated the collection is not empty");

            int count = _count;
            for (int i = 0; i < count; i++)
            {
                HeaderEntry entry = entries[i];

                if (entry.Value is not HeaderStoreItemInfo info)
                {
                    // To retain consistent semantics, we need to upgrade a raw string to a HeaderStoreItemInfo
                    // during enumeration so that we can parse the raw value in order to a) return
                    // the correct set of parsed values, and b) update the instance for subsequent enumerations
                    // to reflect that parsing.
                    info = new HeaderStoreItemInfo() { RawValue = entry.Value };

                    if (EntriesAreLiveView)
                    {
                        entries[i].Value = info;
                    }
                    else
                    {
                        Debug.Assert(ContainsKey(entry.Key));
                        ((Dictionary<HeaderDescriptor, object>)_headerStore!)[entry.Key] = info;
                    }
                }

                // Make sure we parse all raw values before returning the result. Note that this has to be
                // done before we calculate the array length (next line): A raw value may contain a list of
                // values.
                if (!ParseRawHeaderValues(entry.Key, info))
                {
                    // We saw an invalid header value (contains newline chars) and deleted it.

                    // If the HeaderEntry[] we are enumerating is the live header store, the entries have shifted.
                    if (EntriesAreLiveView)
                    {
                        i--;
                        count--;
                    }
                }
                else
                {
                    string[] values = GetStoreValuesAsStringArray(entry.Key, info);
                    yield return new KeyValuePair<string, IEnumerable<string>>(entry.Key.Name, values);
                }
            }
        }

        #endregion

        #region IEnumerable Members

        Collections.IEnumerator Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion

        internal void AddParsedValue(HeaderDescriptor descriptor, object value)
        {
            Debug.Assert(value != null);
            Debug.Assert(descriptor.Parser != null, "Can't add parsed value if there is no parser available.");

            HeaderStoreItemInfo info = GetOrCreateHeaderInfo(descriptor);

            // If the current header has only one value, we can't add another value. The strongly typed property
            // must not call AddParsedValue(), but SetParsedValue(). E.g. for headers like 'Date', 'Host'.
            Debug.Assert(descriptor.Parser.SupportsMultipleValues, $"Header '{descriptor.Name}' doesn't support multiple values");

            AddParsedValue(info, value);
        }

        internal void SetParsedValue(HeaderDescriptor descriptor, object value)
        {
            Debug.Assert(value != null);
            Debug.Assert(descriptor.Parser != null, "Can't add parsed value if there is no parser available.");

            // This method will first clear all values. This is used e.g. when setting the 'Date' or 'Host' header.
            // i.e. headers not supporting collections.
            HeaderStoreItemInfo info = GetOrCreateHeaderInfo(descriptor);

            info.InvalidValue = null;
            info.ParsedValue = null;
            info.RawValue = null;

            AddParsedValue(info, value);
        }

        internal void SetOrRemoveParsedValue(HeaderDescriptor descriptor, object? value)
        {
            if (value == null)
            {
                Remove(descriptor);
            }
            else
            {
                SetParsedValue(descriptor, value);
            }
        }

        public bool Remove(string name) => Remove(GetHeaderDescriptor(name));

        internal bool RemoveParsedValue(HeaderDescriptor descriptor, object value)
        {
            Debug.Assert(value != null);

            // If we have a value for this header, then verify if we have a single value. If so, compare that
            // value with 'item'. If we have a list of values, then remove 'item' from the list.
            if (TryGetAndParseHeaderInfo(descriptor, out HeaderStoreItemInfo? info))
            {
                Debug.Assert(descriptor.Parser != null, "Can't add parsed value if there is no parser available.");
                Debug.Assert(descriptor.Parser.SupportsMultipleValues,
                    "This method should not be used for single-value headers. Use Remove(string) instead.");

                // If there is no entry, just return.
                if (info.ParsedValue == null)
                {
                    return false;
                }

                bool result = false;
                IEqualityComparer? comparer = descriptor.Parser.Comparer;

                List<object>? parsedValues = info.ParsedValue as List<object>;
                if (parsedValues == null)
                {
                    Debug.Assert(info.ParsedValue.GetType() == value.GetType(),
                        "Stored value does not have the same type as 'value'.");

                    if (AreEqual(value, info.ParsedValue, comparer))
                    {
                        info.ParsedValue = null;
                        result = true;
                    }
                }
                else
                {
                    foreach (object item in parsedValues)
                    {
                        Debug.Assert(item.GetType() == value.GetType(),
                            "One of the stored values does not have the same type as 'value'.");

                        if (AreEqual(value, item, comparer))
                        {
                            // Remove 'item' rather than 'value', since the 'comparer' may consider two values
                            // equal even though the default obj.Equals() may not (e.g. if 'comparer' does
                            // case-insensitive comparison for strings, but string.Equals() is case-sensitive).
                            result = parsedValues.Remove(item);
                            break;
                        }
                    }

                    // If we removed the last item in a list, remove the list.
                    if (parsedValues.Count == 0)
                    {
                        info.ParsedValue = null;
                    }
                }

                // If there is no value for the header left, remove the header.
                if (info.IsEmpty)
                {
                    bool headerRemoved = Remove(descriptor);
                    Debug.Assert(headerRemoved, $"Existing header '{descriptor.Name}' couldn't be removed.");
                }

                return result;
            }

            return false;
        }

        internal bool ContainsParsedValue(HeaderDescriptor descriptor, object value)
        {
            Debug.Assert(value != null);

            // If we have a value for this header, then verify if we have a single value. If so, compare that
            // value with 'item'. If we have a list of values, then compare each item in the list with 'item'.
            if (TryGetAndParseHeaderInfo(descriptor, out HeaderStoreItemInfo? info))
            {
                Debug.Assert(descriptor.Parser != null, "Can't add parsed value if there is no parser available.");
                Debug.Assert(descriptor.Parser.SupportsMultipleValues,
                    "This method should not be used for single-value headers. Use equality comparer instead.");

                // If there is no entry, just return.
                if (info.ParsedValue == null)
                {
                    return false;
                }

                List<object>? parsedValues = info.ParsedValue as List<object>;

                IEqualityComparer? comparer = descriptor.Parser.Comparer;

                if (parsedValues == null)
                {
                    Debug.Assert(info.ParsedValue.GetType() == value.GetType(),
                        "Stored value does not have the same type as 'value'.");

                    return AreEqual(value, info.ParsedValue, comparer);
                }
                else
                {
                    foreach (object item in parsedValues)
                    {
                        Debug.Assert(item.GetType() == value.GetType(),
                            "One of the stored values does not have the same type as 'value'.");

                        if (AreEqual(value, item, comparer))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            return false;
        }

        internal virtual void AddHeaders(HttpHeaders sourceHeaders)
        {
            Debug.Assert(sourceHeaders != null);
            Debug.Assert(GetType() == sourceHeaders.GetType(), "Can only copy headers from an instance of the same type.");

            // Only add header values if they're not already set on the message. Note that we don't merge
            // collections: If both the default headers and the message have set some values for a certain
            // header, then we don't try to merge the values.
            if (_count == 0 && sourceHeaders._headerStore is HeaderEntry[] sourceEntries)
            {
                // If the target collection is empty, we don't have to search for existing values
                _count = sourceHeaders._count;
                if (_headerStore is not HeaderEntry[] entries || entries.Length < _count)
                {
                    entries = new HeaderEntry[sourceEntries.Length];
                    _headerStore = entries;
                }

                for (int i = 0; i < _count && i < sourceEntries.Length; i++)
                {
                    HeaderEntry entry = sourceEntries[i];
                    if (entry.Value is HeaderStoreItemInfo info)
                    {
                        entry.Value = CloneHeaderInfo(entry.Key, info);
                    }
                    entries[i] = entry;
                }
            }
            else
            {
                foreach (HeaderEntry entry in sourceHeaders.GetEntries())
                {
                    ref object? storeValueRef = ref GetValueRefOrAddDefault(entry.Key);
                    if (storeValueRef is null)
                    {
                        object sourceValue = entry.Value;
                        if (sourceValue is HeaderStoreItemInfo info)
                        {
                            storeValueRef = CloneHeaderInfo(entry.Key, info);
                        }
                        else
                        {
                            Debug.Assert(sourceValue is string);
                            storeValueRef = sourceValue;
                        }
                    }
                }
            }
        }

        private HeaderStoreItemInfo CloneHeaderInfo(HeaderDescriptor descriptor, HeaderStoreItemInfo sourceInfo)
        {
            var destinationInfo = new HeaderStoreItemInfo
            {
                // Always copy raw values
                RawValue = CloneStringHeaderInfoValues(sourceInfo.RawValue)
            };

            if (descriptor.Parser == null)
            {
                // We have custom header values. The parsed values are strings.
                // Custom header values are always stored as string or list of strings.
                Debug.Assert(sourceInfo.InvalidValue == null, "No invalid values expected for custom headers.");
                destinationInfo.ParsedValue = CloneStringHeaderInfoValues(sourceInfo.ParsedValue);
            }
            else
            {
                // We have a parser, so we also have to copy invalid values and clone parsed values.

                // Invalid values are always strings. Strings are immutable. So we only have to clone the
                // collection (if there is one).
                destinationInfo.InvalidValue = CloneStringHeaderInfoValues(sourceInfo.InvalidValue);

                // Now clone and add parsed values (if any).
                if (sourceInfo.ParsedValue != null)
                {
                    List<object>? sourceValues = sourceInfo.ParsedValue as List<object>;
                    if (sourceValues == null)
                    {
                        CloneAndAddValue(destinationInfo, sourceInfo.ParsedValue);
                    }
                    else
                    {
                        foreach (object item in sourceValues)
                        {
                            CloneAndAddValue(destinationInfo, item);
                        }
                    }
                }
            }

            return destinationInfo;
        }

        private static void CloneAndAddValue(HeaderStoreItemInfo destinationInfo, object source)
        {
            // We only have one value. Clone it and assign it to the store.
            if (source is ICloneable cloneableValue)
            {
                AddParsedValue(destinationInfo, cloneableValue.Clone());
            }
            else
            {
                // If it doesn't implement ICloneable, it's a value type or an immutable type like String/Uri.
                AddParsedValue(destinationInfo, source);
            }
        }

        [return: NotNullIfNotNull("source")]
        private static object? CloneStringHeaderInfoValues(object? source)
        {
            if (source == null)
            {
                return null;
            }

            List<object>? sourceValues = source as List<object>;
            if (sourceValues == null)
            {
                // If we just have one value, return the reference to the string (strings are immutable so it's OK
                // to use the reference).
                return source;
            }
            else
            {
                // If we have a list of strings, create a new list and copy all strings to the new list.
                return new List<object>(sourceValues);
            }
        }

        private HeaderStoreItemInfo GetOrCreateHeaderInfo(HeaderDescriptor descriptor)
        {
            if (TryGetAndParseHeaderInfo(descriptor, out HeaderStoreItemInfo? info))
            {
                return info;
            }
            else
            {
                return CreateAndAddHeaderToStore(descriptor);
            }
        }

        private HeaderStoreItemInfo CreateAndAddHeaderToStore(HeaderDescriptor descriptor)
        {
            Debug.Assert(!ContainsKey(descriptor));

            // If we don't have the header in the store yet, add it now.
            HeaderStoreItemInfo result = new HeaderStoreItemInfo();

            // If the descriptor header type is in _treatAsCustomHeaderTypes, it must be converted to a custom header before calling this method
            Debug.Assert((descriptor.HeaderType & _treatAsCustomHeaderTypes) == 0);

            AddEntryToStore(new HeaderEntry(descriptor, result));

            return result;
        }

        internal bool TryGetHeaderValue(HeaderDescriptor descriptor, [NotNullWhen(true)] out object? value)
        {
            ref object storeValueRef = ref GetValueRefOrNullRef(descriptor);
            if (Unsafe.IsNullRef(ref storeValueRef))
            {
                value = null;
                return false;
            }
            else
            {
                value = storeValueRef;
                return true;
            }
        }

        private bool TryGetAndParseHeaderInfo(HeaderDescriptor key, [NotNullWhen(true)] out HeaderStoreItemInfo? info)
        {
            ref object storeValueRef = ref GetValueRefOrNullRef(key);
            if (!Unsafe.IsNullRef(ref storeValueRef))
            {
                object value = storeValueRef;
                if (value is HeaderStoreItemInfo hsi)
                {
                    info = hsi;
                }
                else
                {
                    Debug.Assert(value is string);
                    storeValueRef = info = new HeaderStoreItemInfo() { RawValue = value };
                }

                return ParseRawHeaderValues(key, info);
            }

            info = null;
            return false;
        }

        private bool ParseRawHeaderValues(HeaderDescriptor descriptor, HeaderStoreItemInfo info)
        {
            // Unlike TryGetHeaderInfo() this method tries to parse all non-validated header values (if any)
            // before returning to the caller.
            Debug.Assert(!info.IsEmpty);
            if (info.RawValue != null)
            {
                List<string>? rawValues = info.RawValue as List<string>;

                if (rawValues == null)
                {
                    ParseSingleRawHeaderValue(descriptor, info);
                }
                else
                {
                    ParseMultipleRawHeaderValues(descriptor, info, rawValues);
                }

                // At this point all values are either in info.ParsedValue, info.InvalidValue, or were removed since they
                // contain newline chars. Reset RawValue.
                info.RawValue = null;

                // During parsing, we removed the value since it contains newline chars. Return false to indicate that
                // this is an empty header.
                if ((info.InvalidValue == null) && (info.ParsedValue == null))
                {
                    // After parsing the raw value, no value is left because all values contain newline chars.
                    Debug.Assert(_count > 0);
                    Remove(descriptor);
                    return false;
                }
            }

            return true;
        }

        private static void ParseMultipleRawHeaderValues(HeaderDescriptor descriptor, HeaderStoreItemInfo info, List<string> rawValues)
        {
            if (descriptor.Parser == null)
            {
                foreach (string rawValue in rawValues)
                {
                    if (!ContainsNewLine(rawValue, descriptor))
                    {
                        AddParsedValue(info, rawValue);
                    }
                }
            }
            else
            {
                foreach (string rawValue in rawValues)
                {
                    if (!TryParseAndAddRawHeaderValue(descriptor, info, rawValue, true))
                    {
                        if (NetEventSource.Log.IsEnabled()) NetEventSource.Log.HeadersInvalidValue(descriptor.Name, rawValue);
                    }
                }
            }
        }

        private static void ParseSingleRawHeaderValue(HeaderDescriptor descriptor, HeaderStoreItemInfo info)
        {
            string? rawValue = info.RawValue as string;
            Debug.Assert(rawValue != null, "RawValue must either be List<string> or string.");

            if (descriptor.Parser == null)
            {
                if (!ContainsNewLine(rawValue, descriptor))
                {
                    AddParsedValue(info, rawValue);
                }
            }
            else
            {
                if (!TryParseAndAddRawHeaderValue(descriptor, info, rawValue, true))
                {
                    if (NetEventSource.Log.IsEnabled()) NetEventSource.Log.HeadersInvalidValue(descriptor.Name, rawValue);
                }
            }
        }

        // See Add(name, string)
        internal bool TryParseAndAddValue(HeaderDescriptor descriptor, string? value)
        {
            // We don't use GetOrCreateHeaderInfo() here, since this would create a new header in the store. If parsing
            // the value then throws, we would have to remove the header from the store again. So just get a
            // HeaderStoreItemInfo object and try to parse the value. If it works, we'll add the header.
            HeaderStoreItemInfo info;
            bool addToStore;
            PrepareHeaderInfoForAdd(descriptor, out info, out addToStore);

            bool result = TryParseAndAddRawHeaderValue(descriptor, info, value, false);

            if (result && addToStore && (info.ParsedValue != null))
            {
                // If we get here, then the value could be parsed correctly. If we created a new HeaderStoreItemInfo, add
                // it to the store if we added at least one value.
                Debug.Assert(!ContainsKey(descriptor));
                AddEntryToStore(new HeaderEntry(descriptor, info));
            }

            return result;
        }

        // See ParseAndAddValue
        private static bool TryParseAndAddRawHeaderValue(HeaderDescriptor descriptor, HeaderStoreItemInfo info, string? value, bool addWhenInvalid)
        {
            Debug.Assert(info != null);
            Debug.Assert(descriptor.Parser != null);

            // Values are added as 'invalid' if we either can't parse the value OR if we already have a value
            // and the current header doesn't support multiple values: e.g. trying to add a date/time value
            // to the 'Date' header if we already have a date/time value will result in the second value being
            // added to the 'invalid' header values.
            if (!info.CanAddParsedValue(descriptor.Parser))
            {
                if (addWhenInvalid)
                {
                    AddInvalidValue(info, value ?? string.Empty);
                }
                return false;
            }

            int index = 0;

            if (descriptor.Parser.TryParseValue(value, info.ParsedValue, ref index, out object? parsedValue))
            {
                // The raw string only represented one value (which was successfully parsed). Add the value and return.
                if ((value == null) || (index == value.Length))
                {
                    if (parsedValue != null)
                    {
                        AddParsedValue(info, parsedValue);
                    }
                    return true;
                }
                Debug.Assert(index < value.Length, "Parser must return an index value within the string length.");

                // If we successfully parsed a value, but there are more left to read, store the results in a temp
                // list. Only when all values are parsed successfully write the list to the store.
                List<object> parsedValues = new List<object>();
                if (parsedValue != null)
                {
                    parsedValues.Add(parsedValue);
                }

                while (index < value.Length)
                {
                    if (descriptor.Parser.TryParseValue(value, info.ParsedValue, ref index, out parsedValue))
                    {
                        if (parsedValue != null)
                        {
                            parsedValues.Add(parsedValue);
                        }
                    }
                    else
                    {
                        if (!ContainsNewLine(value, descriptor) && addWhenInvalid)
                        {
                            AddInvalidValue(info, value);
                        }
                        return false;
                    }
                }

                // All values were parsed correctly. Copy results to the store.
                foreach (object item in parsedValues)
                {
                    AddParsedValue(info, item);
                }
                return true;
            }

            Debug.Assert(value != null);
            if (!ContainsNewLine(value, descriptor) && addWhenInvalid)
            {
                AddInvalidValue(info, value ?? string.Empty);
            }
            return false;
        }

        private static void AddParsedValue(HeaderStoreItemInfo info, object value)
        {
            Debug.Assert(!(value is List<object>),
                "Header value types must not derive from List<object> since this type is used internally to store " +
                "lists of values. So we would not be able to distinguish between a single value and a list of values.");

            AddValueToStoreValue<object>(value, ref info.ParsedValue);
        }

        private static void AddInvalidValue(HeaderStoreItemInfo info, string value)
        {
            AddValueToStoreValue<string>(value, ref info.InvalidValue);
        }

        private static void AddRawValue(HeaderStoreItemInfo info, string value)
        {
            AddValueToStoreValue<string>(value, ref info.RawValue);
        }

        private static void AddValueToStoreValue<T>(T value, ref object? currentStoreValue) where T : class
        {
            // If there is no value set yet, then add current item as value (we don't create a list
            // if not required). If 'info.Value' is already assigned then make sure 'info.Value' is a
            // List<T> and append 'item' to the list.
            if (currentStoreValue == null)
            {
                currentStoreValue = value;
            }
            else
            {
                List<T>? storeValues = currentStoreValue as List<T>;

                if (storeValues == null)
                {
                    storeValues = new List<T>(2);
                    Debug.Assert(currentStoreValue is T);
                    storeValues.Add((T)currentStoreValue);
                    currentStoreValue = storeValues;
                }
                Debug.Assert(value is T);
                storeValues.Add((T)value);
            }
        }

        // Since most of the time we just have 1 value, we don't create a List<object> for one value, but we change
        // the return type to 'object'. The caller has to deal with the return type (object vs. List<object>). This
        // is to optimize the most common scenario where a header has only one value.
        internal object? GetParsedValues(HeaderDescriptor descriptor)
        {
            if (!TryGetAndParseHeaderInfo(descriptor, out HeaderStoreItemInfo? info))
            {
                return null;
            }

            return info.ParsedValue;
        }

        internal virtual bool IsAllowedHeaderName(HeaderDescriptor descriptor) => true;

        private void PrepareHeaderInfoForAdd(HeaderDescriptor descriptor, out HeaderStoreItemInfo info, out bool addToStore)
        {
            if (!IsAllowedHeaderName(descriptor))
            {
                throw new InvalidOperationException(SR.Format(SR.net_http_headers_not_allowed_header_name, descriptor.Name));
            }

            addToStore = false;
            if (!TryGetAndParseHeaderInfo(descriptor, out info!))
            {
                info = new HeaderStoreItemInfo();
                addToStore = true;
            }
        }

        private void ParseAndAddValue(HeaderDescriptor descriptor, HeaderStoreItemInfo info, string? value)
        {
            Debug.Assert(info != null);

            if (descriptor.Parser == null)
            {
                // If we don't have a parser for the header, we consider the value valid if it doesn't contains
                // newline characters. We add the values as "parsed value". Note that we allow empty values.
                CheckContainsNewLine(value);
                AddParsedValue(info, value ?? string.Empty);
                return;
            }

            // If the header only supports 1 value, we can add the current value only if there is no
            // value already set.
            if (!info.CanAddParsedValue(descriptor.Parser))
            {
                throw new FormatException(SR.Format(System.Globalization.CultureInfo.InvariantCulture, SR.net_http_headers_single_value_header, descriptor.Name));
            }

            int index = 0;
            object parsedValue = descriptor.Parser.ParseValue(value, info.ParsedValue, ref index);

            // The raw string only represented one value (which was successfully parsed). Add the value and return.
            // If value is null we still have to first call ParseValue() to allow the parser to decide whether null is
            // a valid value. If it is (i.e. no exception thrown), we set the parsed value (if any) and return.
            if ((value == null) || (index == value.Length))
            {
                // If the returned value is null, then it means the header accepts empty values. i.e. we don't throw
                // but we don't add 'null' to the store either.
                if (parsedValue != null)
                {
                    AddParsedValue(info, parsedValue);
                }
                return;
            }
            Debug.Assert(index < value.Length, "Parser must return an index value within the string length.");

            // If we successfully parsed a value, but there are more left to read, store the results in a temp
            // list. Only when all values are parsed successfully write the list to the store.
            List<object> parsedValues = new List<object>();
            if (parsedValue != null)
            {
                parsedValues.Add(parsedValue);
            }

            while (index < value.Length)
            {
                parsedValue = descriptor.Parser.ParseValue(value, info.ParsedValue, ref index);
                if (parsedValue != null)
                {
                    parsedValues.Add(parsedValue);
                }
            }

            // All values were parsed correctly. Copy results to the store.
            foreach (object item in parsedValues)
            {
                AddParsedValue(info, item);
            }
        }

        private HeaderDescriptor GetHeaderDescriptor(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(SR.net_http_argument_empty_string, nameof(name));
            }

            if (!HeaderDescriptor.TryGet(name, out HeaderDescriptor descriptor))
            {
                throw new FormatException(string.Format(SR.net_http_headers_invalid_header_name, name));
            }

            if ((descriptor.HeaderType & _allowedHeaderTypes) != 0)
            {
                return descriptor;
            }
            else if ((descriptor.HeaderType & _treatAsCustomHeaderTypes) != 0)
            {
                return descriptor.AsCustomHeader();
            }

            throw new InvalidOperationException(SR.Format(SR.net_http_headers_not_allowed_header_name, name));
        }

        internal bool TryGetHeaderDescriptor(string name, out HeaderDescriptor descriptor)
        {
            if (string.IsNullOrEmpty(name))
            {
                descriptor = default;
                return false;
            }

            if (HeaderDescriptor.TryGet(name, out descriptor))
            {
                HttpHeaderType headerType = descriptor.HeaderType;

                if ((headerType & _allowedHeaderTypes) != 0)
                {
                    return true;
                }

                if ((headerType & _treatAsCustomHeaderTypes) != 0)
                {
                    descriptor = descriptor.AsCustomHeader();
                    return true;
                }
            }

            return false;
        }

        internal static void CheckContainsNewLine(string? value)
        {
            if (value == null)
            {
                return;
            }

            if (HttpRuleParser.ContainsNewLine(value))
            {
                throw new FormatException(SR.net_http_headers_no_newlines);
            }
        }

        private static bool ContainsNewLine(string value, HeaderDescriptor descriptor)
        {
            if (HttpRuleParser.ContainsNewLine(value))
            {
                if (NetEventSource.Log.IsEnabled()) NetEventSource.Error(null, SR.Format(SR.net_http_log_headers_no_newlines, descriptor.Name, value));
                return true;
            }
            return false;
        }

        internal static string[] GetStoreValuesAsStringArray(HeaderDescriptor descriptor, HeaderStoreItemInfo info)
        {
            GetStoreValuesAsStringOrStringArray(descriptor, info, out string? singleValue, out string[]? multiValue);
            Debug.Assert(singleValue is not null ^ multiValue is not null);
            return multiValue ?? new[] { singleValue! };
        }

        internal static void GetStoreValuesAsStringOrStringArray(HeaderDescriptor descriptor, object sourceValues, out string? singleValue, out string[]? multiValue)
        {
            HeaderStoreItemInfo? info = sourceValues as HeaderStoreItemInfo;
            if (info is null)
            {
                Debug.Assert(sourceValues is string);
                singleValue = (string)sourceValues;
                multiValue = null;
                return;
            }

            int length = GetValueCount(info);

            Span<string?> values;
            singleValue = null;
            if (length == 1)
            {
                multiValue = null;
                values = MemoryMarshal.CreateSpan(ref singleValue, 1);
            }
            else
            {
                Debug.Assert(length > 1, "The header should have been removed when it became empty");
                values = multiValue = new string[length];
            }

            int currentIndex = 0;
            ReadStoreValues<string?>(values, info.RawValue, null, ref currentIndex);
            ReadStoreValues<object?>(values, info.ParsedValue, descriptor.Parser, ref currentIndex);
            ReadStoreValues<string?>(values, info.InvalidValue, null, ref currentIndex);
            Debug.Assert(currentIndex == length);
        }

        internal static int GetStoreValuesIntoStringArray(HeaderDescriptor descriptor, object sourceValues, [NotNull] ref string[]? values)
        {
            values ??= Array.Empty<string>();

            HeaderStoreItemInfo? info = sourceValues as HeaderStoreItemInfo;
            if (info is null)
            {
                Debug.Assert(sourceValues is string);

                if (values.Length == 0)
                {
                    values = new string[1];
                }

                values[0] = (string)sourceValues;
                return 1;
            }

            int length = GetValueCount(info);

            if (length > 0)
            {
                if (values.Length < length)
                {
                    values = new string[length];
                }

                int currentIndex = 0;
                ReadStoreValues<string?>(values, info.RawValue, null, ref currentIndex);
                ReadStoreValues<object?>(values, info.ParsedValue, descriptor.Parser, ref currentIndex);
                ReadStoreValues<string?>(values, info.InvalidValue, null, ref currentIndex);
                Debug.Assert(currentIndex == length);
            }

            return length;
        }

        private static int GetValueCount(HeaderStoreItemInfo info)
        {
            Debug.Assert(info != null);

            int valueCount = Count<string>(info.RawValue);
            valueCount += Count<string>(info.InvalidValue);
            valueCount += Count<object>(info.ParsedValue);
            return valueCount;

            static int Count<T>(object? valueStore) =>
                valueStore is null ? 0 :
                valueStore is List<T> list ? list.Count :
                1;
        }

        private static void ReadStoreValues<T>(Span<string?> values, object? storeValue, HttpHeaderParser? parser, ref int currentIndex)
        {
            if (storeValue != null)
            {
                List<T>? storeValues = storeValue as List<T>;

                if (storeValues == null)
                {
                    values[currentIndex] = parser == null ? storeValue.ToString() : parser.ToString(storeValue);
                    currentIndex++;
                }
                else
                {
                    foreach (object? item in storeValues)
                    {
                        Debug.Assert(item != null);
                        values[currentIndex] = parser == null ? item.ToString() : parser.ToString(item);
                        currentIndex++;
                    }
                }
            }
        }

        private bool AreEqual(object value, object? storeValue, IEqualityComparer? comparer)
        {
            Debug.Assert(value != null);

            if (comparer != null)
            {
                return comparer.Equals(value, storeValue);
            }

            // We don't have a comparer, so use the Equals() method.
            return value.Equals(storeValue);
        }

        internal sealed class HeaderStoreItemInfo
        {
            internal HeaderStoreItemInfo() { }

            internal object? RawValue;
            internal object? InvalidValue;
            internal object? ParsedValue;

            internal bool CanAddParsedValue(HttpHeaderParser parser)
            {
                Debug.Assert(parser != null, "There should be no reason to call CanAddValue if there is no parser for the current header.");

                // If the header only supports one value, and we have already a value set, then we can't add
                // another value. E.g. the 'Date' header only supports one value. We can't add multiple timestamps
                // to 'Date'.
                // So if this is a known header, ask the parser if it supports multiple values and check whether
                // we already have a (valid or invalid) value.
                // Note that we ignore the rawValue by purpose: E.g. we are parsing 2 raw values for a header only
                // supporting 1 value. When the first value gets parsed, CanAddValue returns true and we add the
                // parsed value to ParsedValue. When the second value is parsed, CanAddValue returns false, because
                // we have already a parsed value.
                return parser.SupportsMultipleValues || ((InvalidValue == null) && (ParsedValue == null));
            }

            internal bool IsEmpty => (RawValue == null) && (InvalidValue == null) && (ParsedValue == null);
        }


        #region Low-level implementation details that work with _headerStore directly

        // Used to store the CollectionsMarshal.GetValueRefOrAddDefault out parameter.
        // This is a workaround for the Roslyn bug where we can't use a discard instead:
        // https://github.com/dotnet/roslyn/issues/56587#issuecomment-934955526
        private static bool s_dictionaryGetValueRefOrAddDefaultExistsDummy;

        private const int InitialCapacity = 4;
        internal const int ArrayThreshold = 64; // Above this threshold, header ordering will not be preserved

        internal HeaderEntry[]? GetEntriesArray()
        {
            object? store = _headerStore;
            if (store is null)
            {
                return null;
            }
            else if (store is HeaderEntry[] entries)
            {
                return entries;
            }
            else
            {
                return GetEntriesFromDictionary();
            }

            HeaderEntry[] GetEntriesFromDictionary()
            {
                var dictionary = (Dictionary<HeaderDescriptor, object>)_headerStore!;
                var entries = new HeaderEntry[dictionary.Count];
                int i = 0;
                foreach (KeyValuePair<HeaderDescriptor, object> entry in dictionary)
                {
                    entries[i++] = new HeaderEntry
                    {
                        Key = entry.Key,
                        Value = entry.Value
                    };
                }
                return entries;
            }
        }

        internal ReadOnlySpan<HeaderEntry> GetEntries()
        {
            return new ReadOnlySpan<HeaderEntry>(GetEntriesArray(), 0, _count);
        }

        internal int Count => _count;

        private bool EntriesAreLiveView => _headerStore is HeaderEntry[];

        private ref object GetValueRefOrNullRef(HeaderDescriptor key)
        {
            ref object valueRef = ref Unsafe.NullRef<object>();

            object? store = _headerStore;
            if (store is HeaderEntry[] entries)
            {
                for (int i = 0; i < _count && i < entries.Length; i++)
                {
                    if (key.Equals(entries[i].Key))
                    {
                        valueRef = ref entries[i].Value;
                        break;
                    }
                }
            }
            else if (store is not null)
            {
                valueRef = ref CollectionsMarshal.GetValueRefOrNullRef(Unsafe.As<Dictionary<HeaderDescriptor, object>>(store), key);
            }

            return ref valueRef;
        }

        private ref object? GetValueRefOrAddDefault(HeaderDescriptor key)
        {
            object? store = _headerStore;
            if (store is HeaderEntry[] entries)
            {
                for (int i = 0; i < _count && i < entries.Length; i++)
                {
                    if (key.Equals(entries[i].Key))
                    {
                        return ref entries[i].Value!;
                    }
                }

                int count = _count;
                _count++;
                if ((uint)count < (uint)entries.Length)
                {
                    entries[count].Key = key;
                    return ref entries[count].Value!;
                }

                return ref GrowEntriesAndAddDefault(key);
            }
            else if (store is null)
            {
                _count++;
                entries = new HeaderEntry[InitialCapacity];
                _headerStore = entries;
                ref HeaderEntry firstEntry = ref MemoryMarshal.GetArrayDataReference(entries);
                firstEntry.Key = key;
                return ref firstEntry.Value!;
            }
            else
            {
                return ref DictionaryGetValueRefOrAddDefault(key);
            }

            ref object? GrowEntriesAndAddDefault(HeaderDescriptor key)
            {
                var entries = (HeaderEntry[])_headerStore!;
                if (entries.Length == ArrayThreshold)
                {
                    return ref ConvertToDictionaryAndAddDefault(key);
                }
                else
                {
                    Array.Resize(ref entries, entries.Length << 1);
                    _headerStore = entries;
                    ref HeaderEntry firstNewEntry = ref entries[entries.Length >> 1];
                    firstNewEntry.Key = key;
                    return ref firstNewEntry.Value!;
                }
            }

            ref object? ConvertToDictionaryAndAddDefault(HeaderDescriptor key)
            {
                var entries = (HeaderEntry[])_headerStore!;
                var dictionary = new Dictionary<HeaderDescriptor, object>(ArrayThreshold);
                _headerStore = dictionary;
                foreach (HeaderEntry entry in entries)
                {
                    dictionary.Add(entry.Key, entry.Value);
                }
                Debug.Assert(dictionary.Count == _count - 1);
                return ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out s_dictionaryGetValueRefOrAddDefaultExistsDummy);
            }

            ref object? DictionaryGetValueRefOrAddDefault(HeaderDescriptor key)
            {
                var dictionary = (Dictionary<HeaderDescriptor, object>)_headerStore!;
                ref object? value = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out s_dictionaryGetValueRefOrAddDefaultExistsDummy);
                if (value is null)
                {
                    _count++;
                }
                return ref value;
            }
        }

        private void AddEntryToStore(HeaderEntry entry)
        {
            Debug.Assert(!ContainsKey(entry.Key));

            if (_headerStore is HeaderEntry[] entries)
            {
                int count = _count;
                if ((uint)count < (uint)entries.Length)
                {
                    entries[count] = entry;
                    _count++;
                    return;
                }
            }

            GetValueRefOrAddDefault(entry.Key) = entry.Value;
        }

        internal bool ContainsKey(HeaderDescriptor key)
        {
            return !Unsafe.IsNullRef(ref GetValueRefOrNullRef(key));
        }

        public void Clear()
        {
            if (_headerStore is HeaderEntry[] entries)
            {
                Array.Clear(entries, 0, _count);
            }
            else
            {
                _headerStore = null;
            }
            _count = 0;
        }

        internal bool Remove(HeaderDescriptor key)
        {
            bool removed = false;

            object? store = _headerStore;
            if (store is HeaderEntry[] entries)
            {
                for (int i = 0; i < _count && i < entries.Length; i++)
                {
                    if (key.Equals(entries[i].Key))
                    {
                        while (i + 1 < _count && (uint)(i + 1) < (uint)entries.Length)
                        {
                            entries[i] = entries[i + 1];
                            i++;
                        }
                        entries[i] = default;
                        removed = true;
                        break;
                    }
                }
            }
            else if (store is not null)
            {
                removed = Unsafe.As<Dictionary<HeaderDescriptor, object>>(store).Remove(key);
            }

            if (removed)
            {
                _count--;
            }

            return removed;
        }

        #endregion // _headerStore implementation
    }
}
