import type { LandingTemplateProps } from './TemplateRegistry';
import { useTranslation } from '@/i18n/useTranslation';
import { LandingFrame, ProductGallery, ProductTitle, ProductCopy, ProductSpecifications, PurchasePanel } from './ProductParts';

export function StorytellingTemplate(props: LandingTemplateProps) {
    const { t } = useTranslation();
    return <LandingFrame props={props} template="storytelling"><main>
        <header className="lp-editorial-intro"><p className="lp-product-eyebrow">{t('landing.intro')}</p><ProductTitle props={props} /><ProductCopy props={props} brief /><a href="#product-story" className="lp-editorial-anchor">{t('landing.story')} ↓</a></header>
        <section id="product-story" className="lp-editorial-story"><ProductGallery props={props} /><div><h2>{t('landing.story')}</h2><ProductCopy props={props} /><ProductSpecifications props={props} /></div></section>
        <section className="lp-editorial-purchase"><h2>{props.data.tenant.storeName}</h2><PurchasePanel props={props} /></section>
    </main></LandingFrame>;
}
