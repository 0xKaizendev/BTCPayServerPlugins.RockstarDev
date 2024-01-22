﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Filters;
using BTCPayServer.HostedServices;
using BTCPayServer.ModelBinders;
using BTCPayServer.Models;
using BTCPayServer.Models.WalletViewModels;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.PointOfSale.Models;
using BTCPayServer.Rating;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.RockstarDev.Plugins.Payroll;

public class PayrollController : Controller
{
    private readonly PullPaymentHostedService _pullPaymentHostedService;
    private readonly UIStorePullPaymentsController _uiStorePullPaymentsController;
    private readonly ApplicationDbContextFactory _dbContextFactory;
    private readonly IEnumerable<IPayoutHandler> _payoutHandlers;
    private readonly StoreRepository _storeRepository;
    private readonly RateFetcher _rateFetcher;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly AppService _appService;

    public PayrollController(PullPaymentHostedService pullPaymentHostedService,
        UIStorePullPaymentsController uiStorePullPaymentsController,
        ApplicationDbContextFactory dbContextFactory,
        IEnumerable<IPayoutHandler> payoutHandlers, StoreRepository storeRepository, RateFetcher rateFetcher, BTCPayNetworkProvider networkProvider,
            AppService appService)
    {
        _pullPaymentHostedService = pullPaymentHostedService;
        _uiStorePullPaymentsController = uiStorePullPaymentsController;
        _dbContextFactory = dbContextFactory;
        _payoutHandlers = payoutHandlers;
        _storeRepository = storeRepository;
        _rateFetcher = rateFetcher;
        _networkProvider = networkProvider;
        _appService = appService;
    }

    private const string CURRENCY = "USD";


    [HttpGet("~/plugins/{storeId}/vouchers")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ListVouchers(string storeId)
    {

        var now = DateTimeOffset.UtcNow;
        await using var ctx = _dbContextFactory.CreateContext();
        var ppsQuery = await ctx.PullPayments
            .Include(data => data.Payouts)
            .Where(p => p.StoreId == storeId && p.Archived == false)
            .OrderByDescending(data => data.StartDate).ToListAsync();

        var vouchers = ppsQuery.Select(pp => (PullPayment: pp, Blob: pp.GetBlob())).Where(blob => blob.Blob.Name.StartsWith("Voucher")).ToList();

        var paymentMethods = await _payoutHandlers.GetSupportedPaymentMethods(HttpContext.GetStoreData());
        if (!paymentMethods.Any())
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "You must enable at least one payment method before creating a voucher.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(UIStoresController.Dashboard), "UIStores", new { storeId });
        }
        return View(vouchers.Select(tuple => new PayrollInvoiceViewModel()
        {
            Amount = tuple.Blob.Limit,
            Currency = tuple.Blob.Currency,
            Id = tuple.PullPayment.Id,
            Name = tuple.Blob.Name,
            Description = tuple.Blob.Description
        }).ToList());
    }

    [HttpGet("~/plugins/payroll/{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> View(string id)
    {
        return NotFound();

        await using var ctx = _dbContextFactory.CreateContext();
        var pp = await ctx.PullPayments
            .Include(data => data.Payouts)
            .SingleOrDefaultAsync(p => p.Id == id && p.Archived == false);

        if (pp == null)
        {
            return NotFound();
        }

        var blob = pp.GetBlob();
        if (!blob.Name.StartsWith("Voucher"))
        {
        }

        var now = DateTimeOffset.UtcNow;
        var store = await _storeRepository.FindStore(pp.StoreId);
        var storeBlob = store.GetStoreBlob();
        var progress = _pullPaymentHostedService.CalculatePullPaymentProgress(pp, now);
        return View(new PayrollInvoiceViewModel()
        {
            Amount = blob.Limit,
            Currency = blob.Currency,
            Id = pp.Id,
            Name = blob.Name
        });
    }




    public class PayrollInvoiceViewModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; }
        public string Description { get; set; }
    }
}