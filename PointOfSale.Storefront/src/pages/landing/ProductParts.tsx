import React, { useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation, getLocalizedField } from '@/i18n/useTranslation';
import type { LandingTemplateProps } from './TemplateRegistry';

export function LandingFrame({ children, props, template }: { children: React.ReactNode; props: LandingTemplateProps; template: string }) {
    const { t, isRTL, direction, toggleLanguage } = useTranslation();
    return <div dir={direction} data-template={template} className={`landing-page lp-product-page landing-page--${props.isDark ? 'dark' : 'light'} lp-${template}`}>
        <header className="lp-product-nav"><Link to="/">{props.data.tenant.storeName}</Link><button onClick={toggleLanguage}>{isRTL ? 'English' : 'العربية'}</button></header>
        {children}
        <footer className="lp-product-footer"><Link to="/">{t('landing.store')}</Link>{props.data.affiliate.isValid && props.data.affiliate.displayName && <span>{t('landing.shared')} {props.data.affiliate.displayName}</span>}</footer>
    </div>;
}

export function ProductImage({ src, alt }: { src?: string; alt: string }) {
    const { t } = useTranslation();
    const [failedSrc, setFailedSrc] = useState<string>();
    return src && failedSrc !== src ? <img src={src} alt={alt} onError={() => setFailedSrc(src)} />
        : <div className="lp-product-placeholder" role="img" aria-label={t('landing.noImage')}><span aria-hidden="true">▧</span><p>{t('landing.noImage')}</p></div>;
}

export function ProductGallery({ props, compact = false }: { props: LandingTemplateProps; compact?: boolean }) {
    const { t, language } = useTranslation();
    const product = props.data.product;
    const images = product.images || [];
    return <div className="lp-product-gallery"><div className="lp-product-image"><ProductImage src={images[props.activeImageIndex] || props.selectedVariant?.imageUrl || product.mainImage} alt={getLocalizedField(product, 'name', language)} /></div>
        {!compact && images.length > 1 && <div className="lp-product-thumbs">{images.map((src, i) => <button key={`${src}-${i}`} aria-label={`${t('landing.viewImage')} ${new Intl.NumberFormat(language).format(i + 1)}`} aria-pressed={props.activeImageIndex === i} onClick={() => props.onImageSelect(i)}><ProductImage src={src} alt="" /></button>)}</div>}
    </div>;
}

export function ProductTitle({ props }: { props: LandingTemplateProps }) {
    const { language } = useTranslation();
    const product = props.data.product;
    return <><p className="lp-product-eyebrow">{[product.categoryName, product.brand].filter(Boolean).join(' · ')}</p><h1>{getLocalizedField(product, 'name', language)}</h1></>;
}

export function ProductCopy({ props, brief = false }: { props: LandingTemplateProps; brief?: boolean }) {
    const { t, language } = useTranslation();
    const copy = getLocalizedField(props.data.product, 'description', language);
    return <p className="lp-product-copy">{copy ? brief ? copy.slice(0, 180) + (copy.length > 180 ? '…' : '') : copy : t('landing.noDescription')}</p>;
}

export function ProductSpecifications({ props, minimal = false }: { props: LandingTemplateProps; minimal?: boolean }) {
    const { t } = useTranslation();
    const product = props.data.product;
    const attributes = props.selectedVariant?.attributes || [];
    const specs = [...(product.brand ? [{ name: t('filters.brand'), value: product.brand }] : []), ...(product.categoryName ? [{ name: t('filters.category'), value: product.categoryName }] : []), ...attributes];
    const features = (product.features || []).filter(Boolean);
    if (!specs.length && !features.length) return null;
    return <section className="lp-product-specs"><h2>{t(minimal ? 'landing.specs' : 'landing.features')}</h2>
        {!!specs.length && <dl>{(minimal ? specs.slice(0, 3) : specs).map((spec, i) => <div key={i}><dt>{spec.name}</dt><dd>{spec.value}</dd></div>)}</dl>}
        {!!features.length && <ul>{(minimal ? features.slice(0, 3) : features).map((feature, i) => <li key={i}>{feature}</li>)}</ul>}
    </section>;
}

export function PurchasePanel({ props }: { props: LandingTemplateProps }) {
    const { t } = useTranslation();
    const product = props.data.product;
    return <div className="lp-product-purchase">
        <div className="lp-product-price" aria-label={t('landing.price')}>{props.formatPrice(props.displayPrice)}{props.discountedPrice !== null && <del>{props.formatPrice(props.effectivePrice)}</del>}</div>
        <p className={`lp-product-stock ${props.isInStock === false ? 'lp-product-stock--out' : ''}`}>{t(props.isInStock === true ? 'landing.inStock' : props.isInStock === false ? 'landing.outOfStock' : 'landing.unknownStock')}</p>
        {product.hasVariants && <fieldset><legend>{t('landing.options')}</legend><div className="lp-product-options">{product.variants.map((variant, index) => <button key={variant.variantId} onClick={() => props.onVariantSelect(variant)} disabled={!variant.inStock} aria-pressed={props.selectedVariant?.variantId === variant.variantId}>{variant.attributes?.map(a => a.value).join(' / ') || variant.sku || `${t('landing.options')} ${index + 1}`}</button>)}</div></fieldset>}
        <button id="lp-buy-now" className="lp-product-buy" onClick={props.onBuyNow} disabled={props.funnelLoading || props.isInStock !== true || (product.hasVariants && !props.selectedVariant)}>{t(props.funnelLoading ? 'landing.processing' : props.isInStock === false ? 'landing.outOfStock' : 'landing.buy')}</button>
        {props.funnelError && <p role="alert" className="lp-product-error">{t('landing.purchaseError')}</p>}
    </div>;
}
