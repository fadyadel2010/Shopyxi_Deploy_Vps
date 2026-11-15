"""Deterministic assertions over the final read-only lifecycle snapshot."""
from collections import Counter, defaultdict
import json
from pathlib import Path

ROOT = Path(__file__).parent
state = json.loads((ROOT / 'state-final-reconciliation-v2.json').read_text())
tables = state['tables']

assert state['database'] == 'affiliate_full_e2e'
assert state['readOnlyRepeatableRead'] is True
assert state['onlySyntheticFixtureData'] is True

order = tables['Orders'][0]
assert order['Id'] == '8f2866d0-cac0-4c0b-bd91-fe6f3c4c5e12'
assert order['Total'] == 1110 and order['PaymentStatus'] == 5
assert order['Status'] == 5 and order['FulfillmentStatus'] == 5

stock = tables['ProductVariantStocks'][0]
reservation = tables['InventoryReservations'][0]
assert stock['Quantity'] == 3 and stock['ReservedQuantity'] == 0 and stock['DamagedQuantity'] == 0
assert reservation['ReservedQuantity'] == 1 and reservation['CommittedQuantity'] == 1
assert reservation['ReturnedQuantity'] == 1 and reservation['DamagedReturnedQuantity'] == 0
assert reservation['CommittedQuantity'] - reservation['ReturnedQuantity'] - reservation['DamagedReturnedQuantity'] == 0
assert len(tables['StockMovements']) == 4
assert sum(1 for row in tables['StockMovements'] if row['MovementType'] == 3) == 1

refund = tables['OrderRefunds'][0]
refund_line = tables['OrderRefundLines'][0]
settlement = tables['PaymentSettlementBatches'][0]
settlement_order = tables['PaymentSettlementOrders'][0]
assert refund['Amount'] == 1110 and refund['IsFullRefund'] is True
assert refund['IsPostSettlementRefund'] is True and refund['FinancialPostingStatus'] == 2
assert refund['FinancialPostingFailureReason'] is None and refund['FinancialPostingJournalId']
assert refund_line['Amount'] == 1100 and refund_line['OrderItemId'] == tables['OrderItems'][0]['Id']
assert settlement['SettlementType'] == 'MANUAL_COD_COLLECTION'
assert settlement['GrossAmount'] == 1110 and settlement['Fees'] == 0 and settlement['OrderCount'] == 1
assert settlement_order['OrderId'] == order['Id'] and settlement_order['OrderTotal'] == 1110
assert refund['SettlementBatchId'] == settlement['Id']
assert refund['SettlementId'] == settlement['SettlementId']

commissions = tables['AffiliateCommissionLedger']
assert Counter(row['EntryType'] for row in commissions) == Counter({0: 1, 1: 1, 2: 1, 3: 1})
assert all(row['CommissionAmount'] == 110 for row in commissions)
assert sum(row['Amount'] for row in tables['AffiliateBalanceLedger']) == 0
assert sorted(row['Status'] for row in tables['AffiliatePayoutRequests']) == [5, 6]
assert all(row['Status'] not in (1, 2) for row in tables['AffiliatePayoutRequests'])
assert len(tables['AffiliateManualPayouts']) == 1 and tables['AffiliateManualPayouts'][0]['Amount'] == 110
assert len(tables['AffiliateManualPayoutReturns']) == 1 and tables['AffiliateManualPayoutReturns'][0]['Amount'] == 110
assert len(tables['AffiliateManualPayoutReturnAllocations']) == 1
assert tables['AffiliateManualPayoutReturnAllocations'][0]['Amount'] == 110
earned = sum(row['CommissionAmount'] for row in commissions if row['EntryType'] == 0)
reversed_amount = sum(row['CommissionAmount'] for row in commissions if row['EntryType'] == 1)
paid_effective = sum(row['Amount'] for row in tables['AffiliateManualPayouts']) - sum(row['Amount'] for row in tables['AffiliateManualPayoutReturns'])
approved = max(0, sum(row['CommissionAmount'] for row in commissions if row['EntryType'] == 2) - reversed_amount - paid_effective)
pending = sum(row['RequestedAmount'] for row in tables['AffiliatePayoutRequests'] if row['Status'] in (1, 2))
available = max(0, approved - pending)
assert earned == reversed_amount == 110 and approved == paid_effective == pending == available == 0
opening = json.loads((ROOT/'state-before-checkout.json').read_text())['tables']['ProductVariantStocks'][0]['Quantity']
assert opening == stock['Quantity']
paid_state = json.loads((ROOT/'state-payout-paid.json').read_text())['tables']
assert paid_state['AffiliatePayoutRequests'][0]['Status'] == 4
assert len(paid_state['LedgerJournalEntries']) == 6 and len(paid_state['AffiliateManualPayouts']) == 1
returned_state = json.loads((ROOT/'state-payout-returned.json').read_text())['tables']
assert returned_state['AffiliatePayoutRequests'][0]['Status'] == 6
assert len(returned_state['LedgerJournalEntries']) == 7 and len(returned_state['AffiliateManualPayoutReturns']) == 1

journals = tables['LedgerJournalEntries']
lines = tables['LedgerLines']
assert len(journals) == 11 and len(lines) == 22
by_journal = defaultdict(list)
for line in lines:
    by_journal[line['JournalEntryId']].append(line)
for journal in journals:
    journal_lines = by_journal[journal['Id']]
    assert journal['Status'] == 1 and len(journal_lines) == 2
    assert sum(line['DebitAmount'] for line in journal_lines) == sum(line['CreditAmount'] for line in journal_lines)
    assert sum(line['DebitAmount'] for line in journal_lines) > 0

sources = Counter(row['SourceType'] for row in journals)
assert sources == Counter({
    'TreasuryTransfer': 2,
    'Order_COD': 1,
    'AffiliateCommissionEarned': 1,
    'Order_COGS': 1,
    'ManualCodSettlement': 1,
    'AffiliateCommissionPaid': 1,
    'AffiliateManualPayoutReturn': 1,
    'Order_Refund': 1,
    'AffiliateCommissionReversed': 1,
    'Order_COGS_Reversal': 1,
})

accounts = {row['AccountCode']: row for row in tables['LedgerAccounts']}
assert len(accounts) == len(tables['LedgerAccounts']) == 33
account_ids = {row['Id'] for row in accounts.values()}
assert len({row['Id'] for row in journals}) == len(journals)
assert len({row['Id'] for row in lines}) == len(lines)
assert set(by_journal) == {row['Id'] for row in journals}
net_by_account = defaultdict(float)
for line in lines:
    assert line['AccountId'] in account_ids
    assert line['TenantId'] == order['TenantId']
    net_by_account[line['AccountId']] += line['DebitAmount'] - line['CreditAmount']
assert all(value == 0 for value in net_by_account.values()), 'Posted lines must independently net to zero per account'
reconciled_codes = ['1010', '1010-BA6E7A8B', '1130', '4020', '5010', '1310', '2105', '6110']
assert all(accounts[code]['CurrentBalance'] == 0 for code in reconciled_codes)
assert all(row['CurrentBalance'] == 0 for row in tables['LedgerAccounts'])
assert tables['Cashboxes'][0]['CurrentBalance'] == 0

outbox = tables['AccountingOutboxMessages']
assert len(outbox) == 5
assert all(row['IsFailed'] is False and row['ProcessedAtUtc'] and row['JournalId'] for row in outbox)
assert all(row['JournalId'] in by_journal for row in outbox)
assert not tables['SyntheticQaOrders'] and not tables['SyntheticQaEvidence'] and not tables['SyntheticQaCommissionPostings']

click = state['mongo']['clicks'][0]
assert click['tenantId'] == order['TenantId']
assert click['affiliateAccountId'] == order['AffiliateAccountId']
assert click['productId'] == tables['Products'][0]['Id']
assert click['referralCode'] == tables['AffiliatePromotionLinks'][0]['ReferralCode']
assert tables['AffiliateAttributions'][0]['OrderId'] == order['Id']
assert tables['AffiliateAttributions'][0]['AffiliateAccountId'] == order['AffiliateAccountId']
assert all(row['OrderId'] == order['Id'] and row['AffiliateAccountId'] == order['AffiliateAccountId'] for row in commissions)
assert tables['CheckoutAffiliateJourneys'][0]['journeys'][0]['affiliateAccountId'] == order['AffiliateAccountId']

summary = {
    'result': 'PASS',
    'snapshot': 'state-final-reconciliation-v2.json',
    'order': {'id': order['Id'], 'total': order['Total'], 'paymentStatus': 'Refunded'},
    'inventory': {'opening': opening, 'final': stock['Quantity'], 'reserved': stock['ReservedQuantity'], 'returned': reservation['ReturnedQuantity']},
    'affiliate': {'historicalEarned': earned, 'approved': approved, 'paidEffective': paid_effective, 'reversed': reversed_amount, 'net': earned-reversed_amount, 'available': available, 'pendingReserved': pending},
    'money': {'mainCashbox': accounts['1010']['CurrentBalance'], 'affiliateCashbox': accounts['1010-BA6E7A8B']['CurrentBalance'], 'codClearing': accounts['1130']['CurrentBalance'], 'affiliatePayable': accounts['2105']['CurrentBalance']},
    'accounting': {'journals': len(journals), 'lines': len(lines), 'allBalanced': True, 'outboxProcessed': len(outbox)},
    'historyPreserved': {'settlements': 1, 'payouts': 1, 'payoutReturns': 1, 'refunds': 1, 'payoutRequests': 2},
    'analyticsProjectionNote': {'clickPersisted': True, 'clickOrderId': click.get('orderId'), 'financialAuthority': 'SQL attribution and ledgers'},
}
(ROOT / 'final-reconciliation-summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8', newline='\n')
print(json.dumps(summary, indent=2))
