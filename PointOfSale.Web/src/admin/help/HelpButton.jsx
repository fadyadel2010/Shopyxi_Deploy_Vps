import { forwardRef } from 'react'
import { CircleHelp } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Badge, Button } from '../../design-system/primitives'

const HelpButton = forwardRef(function HelpButton({ onClick, hasArticle = false }, ref) {
    const { t } = useTranslation()

    return (
        <Button
            ref={ref}
            type="button"
            variant="secondary"
            size="sm"
            onClick={onClick}
            className="pos-help-button"
            title={hasArticle ? t('common.help', 'Help') : t('common.helpUnavailable', 'Help article coming soon')}
            leadingIcon={<CircleHelp size={16} />}
        >
            <span className="pos-help-button__label">
                <span>{t('common.help', 'Help')}</span>
                <Badge variant={hasArticle ? 'success' : 'warning'} size="sm">
                    {hasArticle ? t('common.available', 'Available') : t('common.soon', 'Soon')}
                </Badge>
            </span>
        </Button>
    )
})

export default HelpButton
