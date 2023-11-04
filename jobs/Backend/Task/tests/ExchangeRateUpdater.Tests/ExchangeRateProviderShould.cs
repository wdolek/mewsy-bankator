using ExchangeRateUpdater.Cnb;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ExchangeRateUpdater.Tests;

public class ExchangeRateProviderShould : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ExchangeRateProvider _sut;

    public ExchangeRateProviderShould()
    {
        var options = Options.Create(
            new ExchangeRateProviderOptions
            {
                CacheTtl = TimeSpan.FromMinutes(8)
            });
        
        _httpClient = new HttpClient();
        CnbClient cnbClient = new(_httpClient, NullLogger<CnbClient>.Instance);

        _sut = new ExchangeRateProvider(options, cnbClient);
    }

    [Fact]
    public async Task ReturnExchangeRates()
    {
        // act
        var rates = await _sut.GetExchangeRates(new[] { new Currency("EUR") }, CancellationToken.None);

        // assert
        var rate = Assert.Single(rates);
        Assert.Equal("EUR", rate.SourceCurrency.Code);
        Assert.Equal("CZK", rate.TargetCurrency.Code);

        // let's expect that the exchange rate is positive (⊙_⊙;)
        Assert.True(rate.Value > 0);
    }

    [Fact]
    public async Task NotReturnUnknownCurrency()
    {
        // act
        var rates = await _sut.GetExchangeRates(new[] { new Currency("SPL") }, CancellationToken.None);

        // assert
        // SPL – Seborga Luigino (Principality of Seborga) is not expected to be supported by the CNB
        Assert.Empty(rates);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _sut.Dispose();
    }
}