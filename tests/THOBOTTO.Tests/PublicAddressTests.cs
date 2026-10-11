using System.Net;

using THOBOTTO.GameServers;

namespace THOBOTTO.Tests;

public class PublicAddressTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700::1111")]
    [InlineData("2003::1")]
    [InlineData("2001:1::1")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_pass(string address)
    {
        Assert.True(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("0.0.0.0")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    public void Own_network_v4_addresses_are_refused(string address)
    {
        Assert.False(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    public void Own_network_v6_addresses_are_refused(string address)
    {
        Assert.False(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.0.1")]
    [InlineData("::ffff:192.168.0.1")]
    public void V4_mapped_v6_addresses_are_judged_as_v4(string address)
    {
        Assert.False(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("64:ff9b::8.8.8.8")]
    [InlineData("64:ff9b::10.0.0.1")]
    [InlineData("2002:808:808::1")]
    [InlineData("2002:c0a8:1::1")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [InlineData("::8.8.8.8")]
    [InlineData("::10.0.0.1")]
    public void V6_addresses_carrying_a_v4_one_are_refused(string address)
    {
        Assert.False(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2001:4860:4860::8888", true)]
    public async Task A_literal_address_is_judged_without_a_lookup(string host, bool expected)
    {
        Assert.Equal(expected, await PublicAddress.IsPublicAsync(host, CancellationToken.None));
    }
}
