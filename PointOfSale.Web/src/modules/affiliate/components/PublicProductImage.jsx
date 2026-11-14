import { useState } from 'react'
import { Package } from 'lucide-react'
import { useLanguage } from '../i18n/LanguageContext'

// A missing file must not leave a broken image or trigger a retry loop.
export default function PublicProductImage({ src, alt, ...props }) {
    const { t } = useLanguage()
    const [failedSrc, setFailedSrc] = useState(null)
    return src && failedSrc !== src
        ? <img {...props} src={src} alt={alt} onError={() => setFailedSrc(src)} />
        : <div role="img" aria-label={t('public.noImage')} className="flex flex-col items-center gap-2 text-gray-500">
            <Package aria-hidden="true" className="w-10 h-10" />
            <span className="text-sm">{t('public.noImage')}</span>
        </div>
}
