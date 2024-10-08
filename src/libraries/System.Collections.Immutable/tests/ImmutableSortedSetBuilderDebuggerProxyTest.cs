// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Xunit;

namespace System.Collections.Immutable.Tests
{
    public class ImmutableSortedSetBuilderDebuggerProxyTest : ImmutablesTestBase
    {
        [Fact]
        public void DoesNotCacheContents()
        {
            ImmutableSortedSet<int> set = ImmutableSortedSet<int>.Empty.Add(1);
            ImmutableSortedSet<int>.Builder builder = set.ToBuilder();
            DebuggerTypeProxyAttribute debuggerProxyAttribute = builder.GetType().GetCustomAttribute<DebuggerTypeProxyAttribute>();
            Type debuggerProxyType = Type.GetType(debuggerProxyAttribute.ProxyTypeName).MakeGenericType(typeof(int));
            object debuggerProxy = Activator.CreateInstance(debuggerProxyType, [builder]);
            _ = GetContents(); // view the contents to trigger caching
            builder.Add(2);
            Assert.Equal(builder.ToArray(), GetContents());

            ICollection<int> GetContents() => (ICollection<int>)debuggerProxyType.GetProperty("Contents").GetValue(debuggerProxy);
        }
    }
}
