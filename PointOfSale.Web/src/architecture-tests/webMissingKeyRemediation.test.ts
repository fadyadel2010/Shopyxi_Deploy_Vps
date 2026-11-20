/**
 * @vitest-environment node
 *
 * RB-05R — Web missing-key remediation gate.
 *
 * The RB-05 register (docs/branding/evidence/web-missing-keys/missing-keys.json)
 * classified 284 distinct Web translation keys as genuinely missing from the
 * central catalogs: 145 `storefront-admin`, 24 `settings-reports` and 115
 * `other`. Every call site passes either an inline English default or no
 * default at all, so a missing entry either degrades Arabic to English or
 * renders the raw key to the user.
 *
 * This gate is deliberately source-driven: besides the recorded register it
 * re-derives every literal `t('...')` specifier from non-affiliate Web source
 * and proves it resolves in both catalogs at runtime. A future unlocalized key
 * therefore fails here instead of shipping as machine text.
 */
import { describe, expect, it } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';
import i18next from 'i18next';

const WEB_ROOT = path.resolve(__dirname, '..', '..');
const SRC_ROOT = path.join(WEB_ROOT, 'src');
const LOCALES_DIR = path.join(SRC_ROOT, 'i18n', 'locales');
const EN_FILE = path.join(LOCALES_DIR, 'en.json');
const AR_FILE = path.join(LOCALES_DIR, 'ar.json');

/** Affiliate screens resolve through their own LanguageProvider dictionary, not the central catalogs. */
const AFFILIATE_DIR = path.join(SRC_ROOT, 'modules', 'affiliate');

const ARABIC_PLURAL_SUFFIXES = ['_zero', '_one', '_two', '_few', '_many', '_other'];

type Catalog = Record<string, unknown>;

function readCatalogText(file: string): string {
    return fs.readFileSync(file, 'utf8');
}

function readCatalog(file: string): Catalog {
    return JSON.parse(readCatalogText(file)) as Catalog;
}

const en = readCatalog(EN_FILE);
const ar = readCatalog(AR_FILE);
const enText = readCatalogText(EN_FILE);
const arText = readCatalogText(AR_FILE);

function lookup(catalog: Catalog, key: string): unknown {
    let current: unknown = catalog;
    for (const part of key.split('.')) {
        if (!current || typeof current !== 'object' || !(part in (current as Catalog))) {
            return undefined;
        }
        current = (current as Catalog)[part];
    }
    return current;
}

/**
 * Runtime-accurate presence check: a string, a non-empty array (used by
 * `returnObjects` guides) or a complete plural form set all resolve.
 */
function resolves(catalog: Catalog, key: string): boolean {
    const direct = lookup(catalog, key);
    if (typeof direct === 'string' && direct.trim().length > 0) return true;
    if (Array.isArray(direct)) {
        return direct.length > 0 && direct.every(entry => typeof entry === 'string' && entry.trim().length > 0);
    }
    if (direct && typeof direct === 'object') return true;
    return ARABIC_PLURAL_SUFFIXES.some(suffix => {
        const form = lookup(catalog, key + suffix);
        return typeof form === 'string' && form.trim().length > 0;
    });
}

function flatten(value: unknown, prefix = '', output: Record<string, unknown> = {}): Record<string, unknown> {
    if (Array.isArray(value)) {
        output[prefix] = value;
        return output;
    }
    if (value && typeof value === 'object') {
        for (const [key, child] of Object.entries(value as Catalog)) {
            flatten(child, prefix ? `${prefix}.${key}` : key, output);
        }
        return output;
    }
    output[prefix] = value;
    return output;
}

function placeholders(value: unknown): string[] {
    return [...new Set(String(value).match(/\{\{[^}]+\}\}/g) ?? [])].sort();
}

const arabicPluralBase = (key: string): string | null => {
    const suffix = ARABIC_PLURAL_SUFFIXES.find(candidate => key.endsWith(candidate));
    return suffix ? key.slice(0, -suffix.length) : null;
};

/** Raw duplicate-key detection: JSON.parse silently keeps the last duplicate. */
function duplicateKeys(text: string): string[] {
    const duplicates: string[] = [];
    const stack: Array<{ path: string; seen: Set<string> }> = [];
    let pendingKey: string | null = null;
    let index = 0;
    while (index < text.length) {
        const char = text[index];
        if (char === '"') {
            let cursor = index + 1;
            let raw = '';
            while (cursor < text.length) {
                const current = text[cursor];
                if (current === '\\') {
                    raw += text.slice(cursor, cursor + 2);
                    cursor += 2;
                    continue;
                }
                if (current === '"') break;
                raw += current;
                cursor += 1;
            }
            let after = cursor + 1;
            while (after < text.length && /\s/.test(text[after])) after += 1;
            pendingKey = text[after] === ':' ? (JSON.parse(`"${raw}"`) as string) : null;
            index = cursor + 1;
            continue;
        }
        if (char === '{') {
            const parent = stack[stack.length - 1];
            stack.push({
                path: parent ? (pendingKey ? `${parent.path}.${pendingKey}` : parent.path) : pendingKey ?? '',
                seen: new Set<string>(),
            });
            pendingKey = null;
            index += 1;
            continue;
        }
        if (char === '}') {
            stack.pop();
            index += 1;
            continue;
        }
        if (char === ',') {
            pendingKey = null;
            index += 1;
            continue;
        }
        index += 1;
        if (pendingKey !== null && stack.length) {
            const frame = stack[stack.length - 1];
            if (frame.seen.has(pendingKey)) duplicates.push(`${frame.path}.${pendingKey}`);
            frame.seen.add(pendingKey);
            pendingKey = null;
        }
    }
    return duplicates;
}

function walk(dir: string): string[] {
    return fs.readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            if (['node_modules', 'locales'].includes(entry.name)) return [];
            if (full === AFFILIATE_DIR) return [];
            return walk(full);
        }
        return /\.(js|jsx|ts|tsx)$/.test(entry.name) && !/\.(test|spec)\.(js|jsx|ts|tsx)$/.test(entry.name) ? [full] : [];
    });
}

/** Literal `t('a.b')` specifiers only; interpolated template keys stay out of scope. */
function collectLiteralKeys(files: string[]): Map<string, string[]> {
    const matcher = /(?<![A-Za-z0-9_$])t\(\s*(['"])([A-Za-z0-9_$-]+(?:\.[A-Za-z0-9_$-]+)+)\1/g;
    const found = new Map<string, string[]>();
    for (const file of files) {
        const source = fs.readFileSync(file, 'utf8');
        for (const match of source.matchAll(matcher)) {
            const key = match[2];
            const sites = found.get(key) ?? [];
            sites.push(`${path.relative(WEB_ROOT, file).split(path.sep).join('/')}:${source.slice(0, match.index).split('\n').length}`);
            found.set(key, sites);
        }
    }
    return found;
}

const literalKeys = collectLiteralKeys(walk(SRC_ROOT));

const RB05R_KEYS: string[] = [
    // Storefront Admin — 145 keys
    "CommerceHubEarnings.actions.retry",
    "CommerceHubEarnings.unavailable.loading",
    "CommerceHubEarnings.unavailable.title",
    "CommerceHubPayments.unavailable.loading",
    "ManualPayments.messages.approveTimeout",
    "ManualPayments.messages.configurationRequired",
    "ManualPayments.messages.failedToLoad",
    "ManualPayments.messages.rejectionReasonTooLong",
    "ManualPayments.messages.rejectionReasonTooShort",
    "ManualPayments.messages.rejectTimeout",
    "ManualPayments.rejectionReasonLabel",
    "storefront.cms.navigationEditor.invalidCount",
    "storefront.cms.pageEditor.arabicComplete",
    "storefront.cms.pageEditor.arabicIncomplete",
    "storefront.cms.pageEditor.arabicMissing",
    "storefront.cms.pageEditor.arabicOptional",
    "storefront.cms.pageEditor.complete",
    "storefront.cms.pageEditor.englishRequired",
    "storefront.cms.pageEditor.incomplete",
    "storefront.cms.pageEditor.languages",
    "storefront.cms.pageEditor.removeArabic",
    "storefront.cms.pageList.count",
    "storefront.cms.pageList.savedReadbackFailed",
    "storefront.domains.actionError",
    "storefront.domains.actions",
    "storefront.domains.activityDesc",
    "storefront.domains.activityError",
    "storefront.domains.addDomain",
    "storefront.domains.addDomainDesc",
    "storefront.domains.addRedirect",
    "storefront.domains.archive",
    "storefront.domains.archiveDomain",
    "storefront.domains.archiveDomainDesc",
    "storefront.domains.canonical",
    "storefront.domains.canonicalHelp",
    "storefront.domains.canonicalUrl",
    "storefront.domains.concurrencyConflict",
    "storefront.domains.concurrencyConflictDesc",
    "storefront.domains.createDomain",
    "storefront.domains.createError",
    "storefront.domains.createValidationError",
    "storefront.domains.dateRange",
    "storefront.domains.delete",
    "storefront.domains.deleteDomain",
    "storefront.domains.deleteDomainDesc",
    "storefront.domains.dnsHelp",
    "storefront.domains.dnsVerification",
    "storefront.domains.domainList",
    "storefront.domains.domainListDesc",
    "storefront.domains.domainNotFound",
    "storefront.domains.domainNotFoundDesc",
    "storefront.domains.domainsActivity",
    "storefront.domains.domainType",
    "storefront.domains.draft",
    "storefront.domains.duplicateHostname",
    "storefront.domains.expiringSoon",
    "storefront.domains.failed",
    "storefront.domains.filter",
    "storefront.domains.filterByAction",
    "storefront.domains.filterByActor",
    "storefront.domains.history",
    "storefront.domains.historyError",
    "storefront.domains.historyInfo",
    "storefront.domains.historyNavigate",
    "storefront.domains.hostname",
    "storefront.domains.hostnameHelp",
    "storefront.domains.hostnameLabel",
    "storefront.domains.hostnamePlaceholder",
    "storefront.domains.httpHelp",
    "storefront.domains.infrastructureError",
    "storefront.domains.jobsFailed",
    "storefront.domains.jobsRunning",
    "storefront.domains.jobsSucceeded",
    "storefront.domains.lifecycleState",
    "storefront.domains.loadError",
    "storefront.domains.loadErrorDesc",
    "storefront.domains.manageRedirects",
    "storefront.domains.noActivity",
    "storefront.domains.noActivityDesc",
    "storefront.domains.noContent",
    "storefront.domains.noDomains",
    "storefront.domains.noDomainsDesc",
    "storefront.domains.noHistory",
    "storefront.domains.noHistoryDesc",
    "storefront.domains.noPrimaryDomain",
    "storefront.domains.noRecentDomains",
    "storefront.domains.noRecentDomainsDesc",
    "storefront.domains.noRedirects",
    "storefront.domains.noRedirectsDesc",
    "storefront.domains.notConfigured",
    "storefront.domains.noToken",
    "storefront.domains.notVerified",
    "storefront.domains.onDomain",
    "storefront.domains.page",
    "storefront.domains.pending",
    "storefront.domains.pendingDesc",
    "storefront.domains.pendingVerification",
    "storefront.domains.primary",
    "storefront.domains.primaryDomain",
    "storefront.domains.promote",
    "storefront.domains.promotePrimary",
    "storefront.domains.promotePrimaryDesc",
    "storefront.domains.quickActions",
    "storefront.domains.recentActivity",
    "storefront.domains.recentDomains",
    "storefront.domains.recordName",
    "storefront.domains.recordType",
    "storefront.domains.recordValue",
    "storefront.domains.redirects",
    "storefront.domains.redirectsError",
    "storefront.domains.redirectsNavigate",
    "storefront.domains.redirectSources",
    "storefront.domains.redirectSourcesDesc",
    "storefront.domains.redirectTarget",
    "storefront.domains.redirectType",
    "storefront.domains.refreshToken",
    "storefront.domains.restore",
    "storefront.domains.restoreDomain",
    "storefront.domains.restoreDomainDesc",
    "storefront.domains.retryVerification",
    "storefront.domains.reviewTitle",
    "storefront.domains.rowActions",
    "storefront.domains.searchPlaceholder",
    "storefront.domains.showingResults",
    "storefront.domains.sortBy",
    "storefront.domains.sslHelp",
    "storefront.domains.startVerification",
    "storefront.domains.startVerificationDesc",
    "storefront.domains.status",
    "storefront.domains.timestampFormat",
    "storefront.domains.totalDomains",
    "storefront.domains.type",
    "storefront.domains.unauthorized",
    "storefront.domains.unauthorizedDesc",
    "storefront.domains.verificationFailed",
    "storefront.domains.verificationHelp",
    "storefront.domains.verificationInstructions",
    "storefront.domains.verificationMethod",
    "storefront.domains.verificationNavigate",
    "storefront.domains.verificationStatus",
    "storefront.domains.verificationUrl",
    "storefront.domains.verified",
    "storefront.domains.verifiedDesc",
    "storefront.domains.verifyNow",
    "storefront.domains.viewAll",

    // Settings and Reports — 24 keys
    "apAging.email",
    "apAging.phone",
    "arAging.email",
    "arAging.phone",
    "costAnalysis.invoiceCount",
    "purchaseRegister.gross",
    "purchaseRegister.net",
    "salesSummary.avgInvoice",
    "settings.currencyCode",
    "settings.customCurrency",
    "settings.defaultCompanyName",
    "settings.defaultReceiptFooter",
    "settings.defaultReceiptHeader",
    "settings.defaultTaxRateHelp",
    "settings.logoAlt",
    "settings.logoRecommendedSize",
    "settings.noLogo",
    "settings.receiptFooterPlaceholder",
    "settings.receiptHeaderPlaceholder",
    "settings.receiptPreview",
    "settings.removeLogo",
    "settings.sampleItemOne",
    "settings.sampleItemTwo",
    "settings.unknownCurrencyWarning",

    // Other (POS, sales, audit log, common) — 115 keys
    "accounts.balanceTransferSubtitle",
    "accounts.destinationType",
    "accounts.selectDestination",
    "accounts.selectSource",
    "accounts.sourceType",
    "auditLog.accessDenied",
    "auditLog.accessDeniedMessage",
    "auditLog.invalidJson",
    "auditLog.searchPlaceholder",
    "auditLog.showingPage",
    "auditLog.source",
    "auditLog.system",
    "auditLog.userAgent",
    "auditLog.web",
    "authProviders.setupGuide.facebook.steps",
    "authProviders.setupGuide.google.steps",
    "balanceTransfer.loadError",
    "carrierConfig.blocked",
    "carrierConfig.configureWebhookSecret",
    "carrierConfig.connected",
    "carrierConfig.credentialsSaved",
    "carrierConfig.howItWorks",
    "carrierConfig.localOnlyValidation",
    "carrierConfig.manualBenefit1",
    "carrierConfig.manualBenefit2",
    "carrierConfig.manualBenefit3",
    "carrierConfig.manualBenefit4",
    "carrierConfig.manualDesc",
    "carrierConfig.manualReady",
    "carrierConfig.manualReadyDesc",
    "carrierConfig.manualTitle",
    "carrierConfig.noCredentialsRequired",
    "carrierConfig.notProductionReady",
    "carrierConfig.readyForCheckout",
    "carrierConfig.rotateWebhookSecret",
    "carrierConfig.step1Desc",
    "carrierConfig.step1Title",
    "carrierConfig.step2Desc",
    "carrierConfig.step2Title",
    "carrierConfig.step3Desc",
    "carrierConfig.step3Title",
    "carrierConfig.step4Desc",
    "carrierConfig.step4Title",
    "carrierConfig.toggleWebhookSecretVisibility",
    "carrierConfig.validateLocally",
    "carrierConfig.webhookSecret",
    "carrierConfig.webhookSecretConfigured",
    "carrierConfig.webhookSecretRequired",
    "cashbox.balancedTransferPolicy",
    "cashbox.manageFundsViaTreasury",
    "categories.syncFailed",
    "categories.syncSuccess",
    "checkoutFields.rules.addCondition",
    "checkoutFields.rules.deleteCondition",
    "checkoutFields.thisField",
    "common.created",
    "common.currentBalance",
    "common.dates",
    "common.discard",
    "common.dismissNotification",
    "common.helpLoadFailed",
    "common.notApplicable",
    "common.product",
    "common.saveChanges",
    "common.showLess",
    "common.showMore",
    "common.sku",
    "common.testing",
    "common.unauthorized",
    "common.unsavedChanges",
    "inventoryManagement.adjustVariantHelp",
    "inventoryManagement.transferVariantHelp",
    "inventoryManagement.variantBadge",
    "paymentModal.validation.cartSyncFailed",
    "paymentModal.validation.requestTimedOutBeforeRetry",
    "paymentModal.validation.stockRevalidationFailed",
    "pos.cartChangedInAnotherTab",
    "pos.cartItemCount",
    "pos.keepThisCart",
    "pos.saleHoldFailed",
    "pos.syncOtherTabCart",
    "purchases.creditDueDateRequired",
    "purchases.unitConversionTitle",
    "purchases.unknownProduct",
    "receipt.notes",
    "receipt.originalInvoice",
    "receipt.print",
    "receipt.printError",
    "receipt.printing",
    "receipt.reason",
    "sales.next",
    "sales.previous",
    "salesReturnReport.date",
    "shipping.serviceLevel",
    "shippingAdministration.account.state",
    "shippingSetup.alwaysAvailable",
    "shippingSetup.learnMore",
    "shippingSetup.validateLocally",
    "storefront.unsavedNavigationConfirm",
    "storefrontLive.activeCarts",
    "storefrontLive.activity.addToCart",
    "storefrontLive.activity.beginCheckout",
    "storefrontLive.activity.orderCompleted",
    "storefrontLive.checkoutInProgress",
    "storefrontLive.generalActivity",
    "storefrontLive.liveVisitors",
    "storefrontLive.noRecentActivity",
    "storefrontLive.polling",
    "storefrontLive.recentActivity",
    "storefrontLive.recentActivitySubtitle",
    "storefrontLive.recentOrders",
    "storefrontLive.subtitle",
    "storefrontLive.title",
    "userManagement.roleDialog.permissionCount",
    "whatsapp.safety.messages.statusFetchFailed",

];

describe('RB-05R register coverage', () => {
    it('records the classified residual register unchanged', () => {
        expect(RB05R_KEYS).toHaveLength(284);
        expect(new Set(RB05R_KEYS).size).toBe(284);
    });

    it('resolves every classified key in the English catalog', () => {
        const missing = RB05R_KEYS.filter(key => !resolves(en, key));
        expect(missing).toEqual([]);
    });

    it('resolves every classified key in the Arabic catalog', () => {
        const missing = RB05R_KEYS.filter(key => !resolves(ar, key));
        expect(missing).toEqual([]);
    });

    it('resolves each classified key at its declared nested path', () => {
        const broken = RB05R_KEYS.filter(key => {
            const segments = key.split('.');
            const leaf = segments[segments.length - 1];
            const parent = segments.slice(0, -1).join('.');
            const container = parent ? lookup(en, parent) : en;
            if (!container || typeof container !== 'object') return true;
            if (leaf in (container as Catalog)) return false;
            // Guides are arrays and counts are plural sets: both resolve without a bare key.
            return !ARABIC_PLURAL_SUFFIXES.some(suffix => `${leaf}${suffix}` in (container as Catalog));
        });
        expect(broken).toEqual([]);
    });

    it('keeps English and Arabic coverage identical for the classified keys', () => {
        const englishOnly = RB05R_KEYS.filter(key => resolves(en, key) && !resolves(ar, key));
        const arabicOnly = RB05R_KEYS.filter(key => resolves(ar, key) && !resolves(en, key));
        expect(englishOnly).toEqual([]);
        expect(arabicOnly).toEqual([]);
    });

    it('does not reuse the key identifier as the displayed English text', () => {
        const selfReferential = RB05R_KEYS.filter(key => {
            const value = lookup(en, key);
            if (typeof value !== 'string') return false;
            return value.trim() === key || value.trim() === key.split('.').pop();
        });
        expect(selfReferential).toEqual([]);
    });

    it('writes Arabic entries that are not copies of the English text', () => {
        // Machine-readable codes stay identical by design; placeholder-only
        // templates carry no prose to translate.
        const identicalByDesign = new Set(['common.sku']);
        const prose = (value: string) => /[A-Za-z]/.test(value.replace(/\{\{[^}]+\}\}/g, ''));
        const copied = RB05R_KEYS.filter(key => {
            if (identicalByDesign.has(key)) return false;
            const english = lookup(en, key);
            const arabic = lookup(ar, key);
            if (typeof english !== 'string' || typeof arabic !== 'string') return false;
            if (!prose(english) || !prose(arabic)) return false;
            return english.trim() === arabic.trim();
        });
        expect(copied).toEqual([]);
    });
});

describe('catalog integrity', () => {
    it('parses both catalogs', () => {
        expect(() => JSON.parse(enText)).not.toThrow();
        expect(() => JSON.parse(arText)).not.toThrow();
        expect(Object.keys(en).length).toBeGreaterThan(0);
        expect(Object.keys(ar).length).toBeGreaterThan(0);
    });

    it('contains no duplicate JSON keys', () => {
        expect(duplicateKeys(enText)).toEqual([]);
        expect(duplicateKeys(arText)).toEqual([]);
    });

    it('keeps English and Arabic structurally aligned', () => {
        const flatEn = flatten(en);
        const flatAr = flatten(ar);
        expect(Object.keys(flatAr).filter(key => !(key in flatEn))).toEqual([]);
        expect(Object.keys(flatEn).filter(key => !(key in flatAr))).toEqual([]);
    });

    it('preserves interpolation placeholders between English and Arabic for affected messages', () => {
        const mismatched = RB05R_KEYS.filter(key => {
            const english = flatten(en)[key];
            const arabic = flatten(ar)[key];
            if (typeof english !== 'string' || typeof arabic !== 'string') return false;
            return JSON.stringify(placeholders(english)) !== JSON.stringify(placeholders(arabic));
        });
        expect(mismatched).toEqual([]);
    });

    it('keeps plural structures complete for the affected messages', () => {
        const flatEn = flatten(en);
        const flatAr = flatten(ar);
        const bases = new Set<string>();
        for (const key of RB05R_KEYS) {
            for (const suffix of ARABIC_PLURAL_SUFFIXES) {
                if (key + suffix in flatEn || key + suffix in flatAr) bases.add(key);
            }
            const base = arabicPluralBase(key);
            if (base) bases.add(base);
        }
        const incomplete = [...bases].filter(base => {
            const englishForms = ARABIC_PLURAL_SUFFIXES.filter(suffix => base + suffix in flatEn);
            if (englishForms.length === 0) return false;
            return ARABIC_PLURAL_SUFFIXES.some(suffix => !(base + suffix in flatAr));
        });
        expect(incomplete).toEqual([]);
    });
});

describe('source coverage', () => {
    it('finds literal translation call sites to audit', () => {
        expect(literalKeys.size).toBeGreaterThan(1000);
    });

    it('resolves every literal key used by non-affiliate Web source in both catalogs', () => {
        const unresolved = [...literalKeys.entries()]
            .filter(([key]) => !resolves(en, key) || !resolves(ar, key))
            .map(([key, sites]) => `${key} @ ${sites.join(', ')}`);
        expect(unresolved).toEqual([]);
    });

    it('never renders a raw translation key at runtime', async () => {
        const instance = i18next.createInstance();
        await instance.init({
            resources: { en: { translation: en }, ar: { translation: ar } },
            supportedLngs: ['en', 'ar'],
            fallbackLng: false,
            load: 'languageOnly',
            returnNull: false,
            returnEmptyString: false,
            interpolation: { escapeValue: false },
        });
        const leaks: string[] = [];
        for (const language of ['en', 'ar']) {
            await instance.changeLanguage(language);
            for (const key of literalKeys.keys()) {
                const rendered = instance.t(key, { count: 2, returnObjects: true }) as unknown;
                if (rendered === key || (typeof rendered === 'string' && !rendered.trim()) || rendered == null) {
                    leaks.push(`${language}:${key}`);
                }
            }
        }
        expect(leaks).toEqual([]);
    });
});

describe('earlier localization guarantees', () => {
    const storefrontAnalyticsKeys = [
        'storefrontAnalytics.title',
        'storefrontAnalytics.subtitle',
        'storefrontAnalytics.ranges.today',
        'storefrontAnalytics.ranges.7d',
        'storefrontAnalytics.ranges.30d',
        'storefrontAnalytics.ranges.this_month',
        'storefrontAnalytics.visits',
        'storefrontAnalytics.uniqueSessions',
        'storefrontAnalytics.orders',
        'storefrontAnalytics.storefrontOrders',
        'storefrontAnalytics.revenue',
        'storefrontAnalytics.grossRevenue',
        'storefrontAnalytics.conversionRate',
        'storefrontAnalytics.visitsToOrders',
        'storefrontAnalytics.addToCart',
        'storefrontAnalytics.cartIntent',
        'storefrontAnalytics.checkoutStarted',
        'storefrontAnalytics.checkoutIntent',
        'storefrontAnalytics.abandonedOrders',
        'storefrontAnalytics.abandonedRevenue',
        'storefrontAnalytics.averageOrderValue',
        'storefrontAnalytics.revenuePerOrder',
        'storefrontAnalytics.topSellingProducts',
        'storefrontAnalytics.topSellingProductsSubtitle',
        'storefrontAnalytics.noTopProducts',
        'storefrontAnalytics.unitsSold',
        'storefrontAnalytics.funnelOverview',
        'storefrontAnalytics.funnelOverviewSubtitle',
        'storefrontAnalytics.pageView',
        'storefrontAnalytics.viewProduct',
        'storefrontAnalytics.beginCheckout',
        'storefrontAnalytics.purchaseCompleted',
    ];

    it('keeps the RB-05 Storefront Analytics cluster localized', () => {
        expect(storefrontAnalyticsKeys.filter(key => !resolves(en, key))).toEqual([]);
        expect(storefrontAnalyticsKeys.filter(key => !resolves(ar, key))).toEqual([]);
        expect(ar.storefrontAnalytics).toBeTruthy();
        expect(placeholders(lookup(en, 'storefrontAnalytics.title'))).toEqual([]);
    });

    it('keeps the RB-04 route guard strings localized', () => {
        for (const key of ['common.restoringSession', 'common.validatingVendorAuthority']) {
            expect(resolves(en, key), key).toBe(true);
            expect(resolves(ar, key), key).toBe(true);
        }
    });
});
