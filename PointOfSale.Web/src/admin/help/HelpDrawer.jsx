import { useEffect, useMemo, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import { LoaderCircle, Sparkles } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import api from '../../services/api'
import { Button, Dialog } from '../../design-system/primitives'

const articleLoaders = {
    dashboard: () => import('./helpArticles/dashboard.md?raw'),
    customers: () => import('./helpArticles/customers.md?raw'),
    suppliers: () => import('./helpArticles/suppliers.md?raw'),
    'sales-representatives': () => import('./helpArticles/sales-representatives.md?raw'),
    categories: () => import('./helpArticles/categories.md?raw'),
    units: () => import('./helpArticles/units.md?raw'),
    warehouses: () => import('./helpArticles/warehouses.md?raw'),
    banks: () => import('./helpArticles/banks.md?raw'),
    products: () => import('./helpArticles/products.md?raw'),
    'create-product': () => import('./helpArticles/create-product.md?raw'),
    'edit-product': () => import('./helpArticles/edit-product.md?raw'),
    'product-variants': () => import('./helpArticles/product-variants.md?raw'),
    'product-media': () => import('./helpArticles/product-media.md?raw'),
    'product-templates': () => import('./helpArticles/product-templates.md?raw'),
    'financial-main-accounts': () => import('./helpArticles/financial-main-accounts.md?raw'),
    'financial-sub-accounts': () => import('./helpArticles/financial-sub-accounts.md?raw'),
    'balance-transfer': () => import('./helpArticles/balance-transfer.md?raw'),
    'opening-treasury': () => import('./helpArticles/opening-treasury.md?raw'),
    'opening-customers': () => import('./helpArticles/opening-customers.md?raw'),
    'opening-suppliers': () => import('./helpArticles/opening-suppliers.md?raw'),
    'opening-warehouse-stock': () => import('./helpArticles/opening-warehouse-stock.md?raw'),
    'pos-sales': () => import('./helpArticles/pos-sales.md?raw'),
    'sales-returns': () => import('./helpArticles/sales-returns.md?raw'),
    'purchase-invoice': () => import('./helpArticles/purchase-invoice.md?raw'),
    'purchase-returns': () => import('./helpArticles/purchase-returns.md?raw'),
    'treasury-dashboard': () => import('./helpArticles/treasury-dashboard.md?raw'),
    'treasury-hub': () => import('./helpArticles/treasury-hub.md?raw'),
    'cash-account': () => import('./helpArticles/cash-account.md?raw'),
    'bank-account': () => import('./helpArticles/bank-account.md?raw'),
    'visa-clearing': () => import('./helpArticles/visa-clearing.md?raw'),
    'customer-supplier-payments': () => import('./helpArticles/customer-supplier-payments.md?raw'),
    'customer-supplier-settlement': () => import('./helpArticles/customer-supplier-settlement.md?raw'),
    'outstanding-balances': () => import('./helpArticles/outstanding-balances.md?raw'),
    'inventory-balance': () => import('./helpArticles/inventory-balance.md?raw'),
    'stock-transfers': () => import('./helpArticles/stock-transfers.md?raw'),
    'barcode-printing': () => import('./helpArticles/barcode-printing.md?raw'),
    'expense-categories': () => import('./helpArticles/expense-categories.md?raw'),
    'expenses-movement': () => import('./helpArticles/expenses-movement.md?raw'),
    'expenses-report': () => import('./helpArticles/expenses-report.md?raw'),
    'customer-supplier-statement': () => import('./helpArticles/customer-supplier-statement.md?raw'),
    'bank-statement': () => import('./helpArticles/bank-statement.md?raw'),
    'visa-statement': () => import('./helpArticles/visa-statement.md?raw'),
    'ecommerce-orders': () => import('./helpArticles/ecommerce-orders.md?raw'),
    'blocked-phones': () => import('./helpArticles/blocked-phones.md?raw'),
    'whatsapp-settings': () => import('./helpArticles/whatsapp-settings.md?raw'),
    'cart-recovery': () => import('./helpArticles/cart-recovery.md?raw'),
    'abandoned-checkouts': () => import('./helpArticles/abandoned-checkouts.md?raw'),
    'shipping-setup': () => import('./helpArticles/shipping-setup.md?raw'),
    'online-customers': () => import('./helpArticles/online-customers.md?raw'),
    'auth-providers': () => import('./helpArticles/auth-providers.md?raw'),
    'payment-methods': () => import('./helpArticles/payment-methods.md?raw'),
    'manual-payments': () => import('./helpArticles/manual-payments.md?raw'),
    coupons: () => import('./helpArticles/coupons.md?raw'),
    promotions: () => import('./helpArticles/promotions.md?raw'),
    'smart-offers': () => import('./helpArticles/smart-offers.md?raw'),
    'commerce-ai': () => import('./helpArticles/commerce-ai.md?raw'),
    'pixel-tracking': () => import('./helpArticles/pixel-tracking.md?raw'),
    'conversion-funnels': () => import('./helpArticles/conversion-funnels.md?raw'),
    'marketing-attribution': () => import('./helpArticles/marketing-attribution.md?raw'),
    'campaign-costs': () => import('./helpArticles/campaign-costs.md?raw'),
    'marketing-roi': () => import('./helpArticles/marketing-roi.md?raw'),
    'analytics-settings': () => import('./helpArticles/analytics-settings.md?raw'),
}

const fallbackArticle = `# Help Center

## English

### Overview

Help for this page is not published yet.

### When to use this page

Open this panel whenever you want a quick explanation of the current screen.

### How to use it

Wait for the article for this page, or continue using the page normally.

### Tips

Use the supported pages in this first rollout to see the full help experience.

### Common mistakes

Assuming every page is documented in phase 1.

---

## العربية

### نظرة عامة

لم يتم نشر شرح هذه الصفحة بعد.

### متى تستخدم هذه الصفحة

افتح هذه اللوحة عندما تحتاج شرحًا سريعًا للشاشة الحالية.

### طريقة الاستخدام

انتظر إضافة المقال الخاص بهذه الصفحة، أو تابع العمل بشكل طبيعي.

### نصائح

جرّب الصفحات المدعومة في هذه المرحلة لرؤية تجربة المساعدة الكاملة.

### أخطاء شائعة

الاعتقاد أن جميع الصفحات موثقة في المرحلة الأولى.
`

function escapeRegExp(value) {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

// Reorder parsed top-level sections, never Markdown strings. Unknown structures
// are left intact; the original content also remains the assistant's payload.
export function localeFirstHelpSections({ locale } = {}) {
    return (tree) => {
        if (!locale?.startsWith('ar')) return
        const nodes = tree.children
        const headings = nodes.filter(node => node.type === 'heading' && node.depth === 2)
        const label = node => node.children?.length === 1 && node.children[0].type === 'text'
            ? node.children[0].value : null
        if (headings.length !== 2 || label(headings[0]) !== 'English' || label(headings[1]) !== 'العربية') return
        const english = nodes.indexOf(headings[0])
        const arabic = nodes.indexOf(headings[1])
        const separators = nodes.filter(node => node.type === 'thematicBreak')
        // One shared title, two complete sections, one separator between them.
        if (english !== 1 || nodes[0].type !== 'heading' || nodes[0].depth !== 1
            || separators.length !== 1 || nodes[arabic - 1] !== separators[0]
            || arabic <= english + 2 || arabic === nodes.length - 1) return
        tree.children = [
            ...nodes.slice(0, english),
            ...nodes.slice(arabic),
            separators[0],
            ...nodes.slice(english, arabic - 1),
        ]
    }
}

function extractSectionContent(markdown, heading) {
    if (!markdown || !heading) {
        return ''
    }

    const pattern = new RegExp(`${escapeRegExp(heading)}\\s*\\n([\\s\\S]*?)(?=\\n## |\\n### |\\n---|$)`, 'i')
    const match = markdown.match(pattern)
    return match?.[1]?.replace(/\n+/g, ' ').trim() ?? ''
}

function getPageDescription(markdown, isRTL, pathname) {
    const preferred = isRTL
        ? extractSectionContent(markdown, '### نظرة عامة') || extractSectionContent(markdown, '### Overview')
        : extractSectionContent(markdown, '### Overview') || extractSectionContent(markdown, '### نظرة عامة')

    if (preferred) {
        return preferred
    }

    return pathname || 'Current admin page'
}

function getModuleName(articleKey, pathname) {
    if (articleKey) {
        return articleKey
    }

    const segments = (pathname || '').split('/').filter(Boolean)
    return segments[0] || 'dashboard'
}

function detectIntent(question) {
    const normalized = (question || '').trim().toLowerCase()

    if (!normalized) {
        return 'general'
    }

    if (
        normalized.includes('how')
        || normalized.includes('ازاي')
        || normalized.includes('كيفية')
        || normalized.includes('طريقة')
        || normalized.includes('خطوات')
    ) {
        return 'how_to'
    }

    if (
        normalized.includes('ليه')
        || normalized.includes('مش')
        || normalized.includes('error')
        || normalized.includes('not working')
    ) {
        return 'troubleshooting'
    }

    return 'general'
}

function normalizeUiText(value) {
    return (value || '').replace(/\s+/g, ' ').trim()
}

function truncateUiText(value, maxLength = 240) {
    const normalized = normalizeUiText(value)
    if (normalized.length <= maxLength) {
        return normalized
    }

    return `${normalized.slice(0, maxLength - 1)}…`
}

function collectVisibleUiContext() {
    if (typeof document === 'undefined') {
        return null
    }

    const main = document.querySelector('main')
    if (!main) {
        return null
    }

    const headingElements = Array.from(main.querySelectorAll('h1, h2, h3'))
    const headings = headingElements
        .map((element) => truncateUiText(element.textContent, 120))
        .filter(Boolean)
        .slice(0, 8)

    const fields = Array.from(main.querySelectorAll('input, select, textarea'))
        .map((element) => {
            const label = truncateUiText(
                element.getAttribute('aria-label')
                || element.getAttribute('name')
                || element.getAttribute('id')
                || element.getAttribute('placeholder')
                || element.labels?.[0]?.textContent
                || element.closest('label')?.textContent,
                80
            )

            if (!label) {
                return null
            }

            let value = ''
            if (element instanceof HTMLInputElement) {
                if (element.type === 'password' || element.type === 'hidden') {
                    return null
                }

                if (element.type === 'checkbox' || element.type === 'radio') {
                    value = element.checked ? 'true' : 'false'
                } else {
                    value = element.value
                }
            } else if (element instanceof HTMLSelectElement || element instanceof HTMLTextAreaElement) {
                value = element.value
            }

            value = truncateUiText(value, 120)
            if (!value) {
                return null
            }

            return { label, value }
        })
        .filter(Boolean)
        .slice(0, 12)

    const buttons = Array.from(main.querySelectorAll('button'))
        .map((button) => truncateUiText(button.textContent, 80))
        .filter(Boolean)
        .slice(0, 10)

    const visibleTextPreview = truncateUiText(main.innerText, 2500)

    return {
        headings,
        fields,
        buttons,
        visibleTextPreview,
    }
}

function buildUiContext({ articleKey, pathname, pageDescription, hasArticle, question }) {
    const visibleUi = collectVisibleUiContext()

    return JSON.stringify(
        {
            currentRoute: pathname || '/',
            currentHelpArticleKey: articleKey || null,
            helpArticleLoaded: hasArticle,
            currentPageSummary: pageDescription || null,
            questionLength: (question || '').trim().length,
            helpPanelOpen: true,
            visibleUi,
        },
        null,
        2
    )
}

export default function HelpDrawer({ isOpen, onClose, articleKey, pathname, restoreFocusRef }) {
    const { t, i18n } = useTranslation()
    const [content, setContent] = useState('')
    const [isLoading, setIsLoading] = useState(false)
    const [error, setError] = useState('')
    const [assistantQuestion, setAssistantQuestion] = useState('')
    const [assistantAnswer, setAssistantAnswer] = useState('')
    const [assistantError, setAssistantError] = useState('')
    const [isAskingAssistant, setIsAskingAssistant] = useState(false)
    const isRTL = i18n.language === 'ar'
    const markdownPlugins = useMemo(
        () => [[localeFirstHelpSections, { locale: i18n.resolvedLanguage || i18n.language }]],
        [i18n.resolvedLanguage, i18n.language]
    )

    const resolvedTitle = useMemo(() => {
        if (articleKey) {
            return t('common.help', 'Help')
        }

        return t('common.helpCenter', 'Help Center')
    }, [articleKey, t])

    const pageDescription = useMemo(
        () => getPageDescription(content, isRTL, pathname),
        [content, isRTL, pathname]
    )

    const moduleName = useMemo(
        () => getModuleName(articleKey, pathname),
        [articleKey, pathname]
    )

    useEffect(() => {
        let isCancelled = false

        if (!isOpen) {
            return () => {
                isCancelled = true
            }
        }

        const loadArticle = async () => {
            const loader = articleKey ? articleLoaders[articleKey] : null

            if (!loader) {
                setContent(fallbackArticle)
                setError('')
                setIsLoading(false)
                return
            }

            setIsLoading(true)
            setError('')

            try {
                const module = await loader()
                if (!isCancelled) {
                    setContent(module.default)
                }
            } catch (loadError) {
                console.error('Failed to load help article:', loadError)
                if (!isCancelled) {
                    setError(t('common.helpLoadFailed', 'Failed to load help article.'))
                    setContent(fallbackArticle)
                }
            } finally {
                if (!isCancelled) {
                    setIsLoading(false)
                }
            }
        }

        loadArticle()

        return () => {
            isCancelled = true
        }
    }, [articleKey, isOpen, pathname, t])

    useEffect(() => {
        setAssistantQuestion('')
        setAssistantAnswer('')
        setAssistantError('')
    }, [isOpen, articleKey, pathname])

    const handleAskAssistant = async (event) => {
        event.preventDefault()

        if (!assistantQuestion.trim() || isAskingAssistant) {
            return
        }

        setIsAskingAssistant(true)
        setAssistantError('')
        setAssistantAnswer('')

        try {
            const intent = detectIntent(assistantQuestion)
            const response = await api.post('/admin/ai/help-assistant/ask', {
                question: assistantQuestion.trim(),
                helpArticleContent: (content || fallbackArticle).trim(),
                route: pathname || '/',
                module: moduleName,
                pageDescription,
                uiContext: buildUiContext({
                    articleKey,
                    pathname,
                    pageDescription,
                    hasArticle: Boolean(articleKey),
                    question: assistantQuestion,
                }),
                intent,
            })

            setAssistantAnswer(response.data?.answer || '')
        } catch (requestError) {
            console.error('Failed to ask contextual help assistant:', requestError)
            setAssistantError(
                requestError?.response?.data?.message
                || (isRTL
                    ? 'تعذر الحصول على رد الآن. حاول مرة أخرى بعد قليل.'
                    : 'Unable to get a response right now. Please try again shortly.')
            )
        } finally {
            setIsAskingAssistant(false)
        }
    }

    return (
        <Dialog
            open={Boolean(isOpen)}
            onOpenChange={(next) => {
                if (!next) {
                    onClose?.()
                }
            }}
            title={resolvedTitle}
            closeOnOverlayClick
            closeOnEscape
            restoreFocusRef={restoreFocusRef}
            className="pos-help-drawer"
        >
            <div className="pos-help__content">
                <div>
                    <p className="pos-help__eyebrow">{t('common.helpCenter', 'Help Center')}</p>
                </div>

                {isLoading ? (
                    <div className="pos-help__loading">
                        <LoaderCircle className="pos-help__spinner" aria-hidden="true" />
                        <p>{t('common.loading', 'Loading...')}</p>
                    </div>
                ) : (
                    <>
                        {error ? (
                            <div className="pos-help__error">{error}</div>
                        ) : null}

                        <div className="pos-help__markdown">
                            <ReactMarkdown
                                remarkPlugins={markdownPlugins}
                                components={{
                                    h1: ({ children }) => <h1 dir="auto" className="pos-help__md-h1">{children}</h1>,
                                    h2: ({ children }) => <h2 dir="auto" className="pos-help__md-h2">{children}</h2>,
                                    h3: ({ children }) => <h3 dir="auto" className="pos-help__md-h3">{children}</h3>,
                                    h4: ({ children }) => <h4 dir="auto">{children}</h4>,
                                    h5: ({ children }) => <h5 dir="auto">{children}</h5>,
                                    h6: ({ children }) => <h6 dir="auto">{children}</h6>,
                                    blockquote: ({ children }) => <blockquote dir="auto">{children}</blockquote>,
                                    p: ({ children }) => <p dir="auto" className="pos-help__md-p">{children}</p>,
                                    ul: ({ children }) => <ul dir="auto" className="pos-help__md-list">{children}</ul>,
                                    ol: ({ children, start }) => <ol dir="auto" start={start} className="pos-help__md-list">{children}</ol>,
                                    li: ({ children }) => <li dir="auto">{children}</li>,
                                    hr: () => <hr className="pos-help__md-hr" />,
                                    strong: ({ children }) => <strong className="pos-help__md-strong">{children}</strong>,
                                    code: ({ children }) => <code className="pos-help__md-code">{children}</code>,
                                }}
                            >
                                {content}
                            </ReactMarkdown>
                        </div>

                        <section className="pos-help__assistant">
                            <div className="pos-help__assistant-head">
                                <div className="pos-help__assistant-icon" aria-hidden="true">
                                    <Sparkles size={16} />
                                </div>
                                <div>
                                    <h3 className="pos-help__assistant-title">
                                        {isRTL ? 'اسأل المساعد' : 'Ask the assistant'}
                                    </h3>
                                    <p className="pos-help__assistant-copy">
                                        {isRTL
                                            ? 'الرد يعتمد فقط على المقال الحالي ومسار الصفحة المفتوحة.'
                                            : 'Answers use only the current help article and page context.'}
                                    </p>
                                </div>
                            </div>

                            <form className="pos-help__form" onSubmit={handleAskAssistant}>
                                <textarea
                                    value={assistantQuestion}
                                    onChange={(event) => setAssistantQuestion(event.target.value)}
                                    rows={3}
                                    maxLength={2000}
                                    aria-label={isRTL ? 'سؤال المساعد' : 'Assistant question'}
                                    className="pos-help__textarea"
                                    placeholder={isRTL
                                        ? 'اكتب سؤالك عن هذه الصفحة أو المشكلة التي تواجهها'
                                        : 'Ask about this page or describe the issue you are facing'}
                                />

                                <div className="pos-help__actions">
                                    <p className="pos-help__hint">
                                        {isRTL
                                            ? 'مثال: لماذا لا يظهر المساعد في المتجر؟'
                                            : 'Example: Why is the assistant not showing on the storefront?'}
                                    </p>

                                    <Button
                                        type="submit"
                                        size="sm"
                                        loading={isAskingAssistant}
                                        disabled={!assistantQuestion.trim()}
                                    >
                                        {isRTL ? 'اسأل' : 'Ask'}
                                    </Button>
                                </div>
                            </form>

                            {assistantError ? (
                                <div className="pos-help__assistant-error">{assistantError}</div>
                            ) : null}

                            {assistantAnswer ? (
                                <div className="pos-help__answer">
                                    <p className="pos-help__answer-label">
                                        {isRTL ? 'الرد' : 'Answer'}
                                    </p>
                                    <p className="pos-help__answer-body">{assistantAnswer}</p>
                                </div>
                            ) : null}
                        </section>
                    </>
                )}
            </div>
        </Dialog>
    )
}
