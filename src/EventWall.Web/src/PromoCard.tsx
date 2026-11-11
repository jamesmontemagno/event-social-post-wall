import { Component } from 'react'
import type { ReactNode } from 'react'
import { QRCodeSVG } from 'qrcode.react'
import type { Promo } from './shared'

class QrBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false }
  static getDerivedStateFromError() { return { failed: true } }
  render() {
    return this.state.failed
      ? <p role="alert">This URL is too complex for a QR code. Use the link instead, or ask staff to shorten the URL.</p>
      : this.props.children
  }
}

export default function PromoCard({ promo }: { promo: Pick<Promo, 'title' | 'description' | 'url'> }) {
  return <article className="promo-card">
    <h3 className="promo-title">{promo.title}</h3>
    <p className="promo-text">{promo.description}</p>
    {promo.url && <>
      <a className="promo-url" href={promo.url} title={promo.url} target="_blank" rel="noopener noreferrer">{promo.url}</a>
      <div className={`promo-qr${promo.url.length > 500 ? ' promo-qr-large' : ''}`}>
        <QrBoundary key={promo.url}>
          <QRCodeSVG value={promo.url} size={promo.url.length > 500 ? 384 : 200} level="L" marginSize={4}
            bgColor="#ffffff" fgColor="#000000" role="img" aria-label={`QR code for ${promo.url}`} />
        </QrBoundary>
      </div>
    </>}
  </article>
}
