import type { LandingTemplateProps } from './TemplateRegistry';
import { useTranslation } from '@/i18n/useTranslation';
import { LandingFrame, ProductGallery, ProductTitle, ProductCopy, ProductSpecifications, PurchasePanel } from './ProductParts';

export function ClassicTemplate(props: LandingTemplateProps) {
    const { t } = useTranslation();
    return <LandingFrame props={props} template="classic"><main className="lp-product-classic">
        <ProductGallery props={props} />
        <div className="lp-product-details"><ProductTitle props={props} /><PurchasePanel props={props} /><section><h2>{t('landing.about')}</h2><ProductCopy props={props} /></section><ProductSpecifications props={props} /></div>
    </main></LandingFrame>;
}
