﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ExchangeRateUpdater.Cnb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using W4k.Either;

namespace ExchangeRateUpdater;

public sealed class ExchangeRateProvider : IDisposable
{
    private readonly CnbClientCacheProxy _cnbClientCache;
    private readonly ILogger<ExchangeRateProvider> _logger;

    public ExchangeRateProvider(IOptions<ExchangeRateProviderOptions> options, ICnbClient cnbClient, ILogger<ExchangeRateProvider> logger)
    {
        // 💡 this check is bit silly since we control the creation of provider, but public type is public type ¯\_(ツ)_/¯
        ArgumentNullException.ThrowIfNull(cnbClient);
        ArgumentNullException.ThrowIfNull(logger);

        _cnbClientCache = new CnbClientCacheProxy(cnbClient, options.Value.CacheTtl);
        _logger = logger;
    }

    public void Dispose()
    {
        _cnbClientCache.Dispose();
    }

    /// <summary>
    /// Should return exchange rates among the specified currencies that are defined by the source. But only those defined
    /// by the source, do not return calculated exchange rates. E.g. if the source contains "CZK/USD" but not "USD/CZK",
    /// do not return exchange rate "USD/CZK" with value calculated as 1 / "CZK/USD". If the source does not provide
    /// some of the currencies, ignore them.
    /// </summary>
    public async Task<Either<IReadOnlyCollection<ExchangeRate>, AppError>> GetExchangeRates(
        IReadOnlyCollection<Currency> currencies,
        CancellationToken cancellationToken)
    {
        var exchangeRatesResult = await _cnbClientCache.GetExchangeRates(cancellationToken);

        return exchangeRatesResult.Match(
            (Currencies: currencies, Logger: _logger),
            static (state, rates) => PickExchangeRates(rates, state.Currencies, state.Logger),
            static (_, _) => new AppError("Failed to fetch exchange rates"));
    }

    private static Either<IReadOnlyCollection<ExchangeRate>, AppError> PickExchangeRates(
        CnbExchangeRatesDto exchangeRates,
        IReadOnlyCollection<Currency> expectedCurrencies,
        ILogger logger) =>
        new ExchangeRateTransformer(logger).GetExchangeRatesForCurrencies(expectedCurrencies, exchangeRates);
}

internal readonly ref struct ExchangeRateTransformer(ILogger logger)
{
    private static readonly Currency DefaultTargetCurrency = new("CZK");

    // 💡 there are other algorithms to solve this, though it looks like there's no universal one (covering various lengths of input)
    //    see benchmarks for other (more readable) algorithms ( •_•)>⌐■-■
    public List<ExchangeRate> GetExchangeRatesForCurrencies(IReadOnlyCollection<Currency> currencies, CnbExchangeRatesDto exchangeRates)
    {
        var currenciesSpan = currencies switch
        {
            Currency[] a => a.AsSpan(),
            List<Currency> l => CollectionsMarshal.AsSpan(l),
            _ => CollectionsMarshal.AsSpan(currencies.ToList()),
        };

        var exchangeRatesSpan = exchangeRates.Rates switch
        {
            List<CnbExchangeRate> l => CollectionsMarshal.AsSpan(l),
            _ => CollectionsMarshal.AsSpan(exchangeRates.Rates.ToList()),
        };

        currenciesSpan.Sort(CurrencyComparer.Instance);
        exchangeRatesSpan.Sort(ExchangeRateCurrencyComparer.Instance);

        var exchangeRatesLength = exchangeRatesSpan.Length;
        var rates = new List<ExchangeRate>(currenciesSpan.Length);

        int rateIdx = 0;
        for (int currencyIdx = 0; currencyIdx < currenciesSpan.Length; currencyIdx++)
        {
            while (rateIdx < exchangeRatesLength
                   && string.CompareOrdinal(exchangeRatesSpan[rateIdx].CurrencyCode, currenciesSpan[currencyIdx].Code) < 0)
            {
                ++rateIdx;
            }

            if (rateIdx < exchangeRatesLength)
            {
                var rate = exchangeRatesSpan[rateIdx];
                if (rate.CurrencyCode == currenciesSpan[currencyIdx].Code)
                {
                    rates.Add(MapToDomain(rate));
                }
                else
                {
                    logger.LogWarning("Currency '{CurrencyCode}' not found in exchange rates", rate.CurrencyCode);
                }
            }
        }

        return rates;
    }

    // 💡 mapping could be moved to separate (static) class, for simplicity I kept it here as this is only use so far
    //   (definitely no space for AutoMapper *wink*)
    private static ExchangeRate MapToDomain(CnbExchangeRate rate) =>
        new(
            sourceCurrency: new Currency(rate.CurrencyCode),
            targetCurrency: DefaultTargetCurrency,
            value: rate.ExchangeRate / rate.Amount);

    private class CurrencyComparer : IComparer<Currency>
    {
        public static readonly CurrencyComparer Instance = new();
        public int Compare(Currency? x, Currency? y) => string.CompareOrdinal(x!.Code, y!.Code);
    }

    private class ExchangeRateCurrencyComparer : IComparer<CnbExchangeRate>
    {
        public static readonly ExchangeRateCurrencyComparer Instance = new();
        public int Compare(CnbExchangeRate? x, CnbExchangeRate? y) => string.CompareOrdinal(x!.CurrencyCode, y!.CurrencyCode);
    }
}