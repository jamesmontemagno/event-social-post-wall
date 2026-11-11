import { useEffect, useRef } from 'react'
import type { Post, PostImage } from './shared'
import PostImages from './PostImages'

export default function ImagePreview({ post, image, onClose }: { post: Post; image: PostImage; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const element = dialog.current
    element?.showModal()
    return () => element?.close()
  }, [])

  function close() {
    dialog.current?.close()
    onClose()
  }

  return <dialog ref={dialog} className="post-dialog" aria-label="Image preview" aria-describedby="image-preview-caption"
    onCancel={event => { event.preventDefault(); close() }} onClick={close}>
    <section className="post-modal image-preview-modal" onClick={event => event.stopPropagation()}>
      <header className="modal-actions">
        <h2>Image preview</h2>
        <button className="modal-close" aria-label="Close image preview" onClick={close} autoFocus>×</button>
      </header>
      <div className="image-preview-content">
        <PostImages post={{ ...post, images: [image] }} expanded />
        <p id="image-preview-caption">{image.alt?.trim() || `Image attached to a post by ${post.authorName}`}</p>
      </div>
    </section>
  </dialog>
}
