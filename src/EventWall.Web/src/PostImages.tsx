import { useState } from 'react'
import type { Post, PostImage } from './shared'

function AttachedImage({ image, previewLabel, onPreview }: { image: PostImage; previewLabel: string; onPreview?: () => void }) {
  const { url, alt } = image
  const [failedUrl, setFailedUrl] = useState<string>()
  if (failedUrl === url) return <span className="post-image-unavailable">Image unavailable</span>
  const photo = <img src={url} alt={alt?.trim() || 'Image attached to this post'} loading="lazy"
    referrerPolicy="no-referrer" onError={() => setFailedUrl(url)} />
  return onPreview
    ? <button type="button" className="post-image-preview" aria-label={previewLabel} aria-haspopup="dialog" onClick={onPreview}>{photo}</button>
    : photo
}

export default function PostImages({ post, expanded = false, onPreview }: { post: Post; expanded?: boolean; onPreview?: (image: PostImage) => void }) {
  const images = post.images?.slice(0, 4)
  if (!images?.length) return null
  return <span className={`post-images ${expanded ? 'post-images-expanded' : ''} image-count-${images.length}`}
    role="group" aria-label={`${images.length} attached ${images.length === 1 ? 'image' : 'images'}`}>
    {images.map((image, index) => <AttachedImage key={`${image.url}-${index}`} image={image}
      previewLabel={`Preview image ${index + 1} of ${images.length} by ${post.authorName}`}
      onPreview={onPreview ? () => onPreview(image) : undefined} />)}
  </span>
}
