import { Vector3, Quaternion } from 'three';

// The camera the way the game's first-person view works: the left mouse button held turns the view where
// the camera stands (yaw and pitch about the camera's own axes), Q and E roll it. The wheel moves up to or back
// from the point looked at, the right or middle button slides the camera across the view.
//
// `target` is the point looked at, `dist` away straight ahead: the rest of the page moves it (following a
// track, flying up to something) and the camera then turns to face it, keeping its roll.
export class FreeLookControls {
  constructor(camera, element) {
    this.camera = camera;
    this.element = element;
    this.target = new Vector3();
    this.lookSpeed = 0.0035;          // radians per pixel
    this.rollSpeed = 1.6;             // radians per second while Q or E is held
    this.zoomStep = 0.88;             // the distance per wheel notch
    this.minDistance = 2;
    this.maxDistance = 1.5e9;
    this._dist = 500;
    this._lastTarget = new Vector3(NaN, NaN, NaN);
    this._drag = null;                // { button, x, y, moved }
    this._dragged = false;
    this._roll = 0;                   // -1, 0, 1: Q, nothing, E
    this._keys = new Set();
    this._v = new Vector3();
    this._q = new Quaternion();

    element.addEventListener('contextmenu', (e) => e.preventDefault());
    element.addEventListener('pointerdown', (e) => this._down(e));
    window.addEventListener('pointermove', (e) => this._move(e));
    window.addEventListener('pointerup', (e) => this._up(e));
    element.addEventListener('wheel', (e) => this._wheel(e), { passive: false });
    window.addEventListener('keydown', (e) => this._key(e, true));
    window.addEventListener('keyup', (e) => this._key(e, false));
    window.addEventListener('blur', () => { this._keys.clear(); this._drag = null; });
  }

  /// Whether the last press was a drag rather than a click (asked once, by the click handler).
  consumeDrag() {
    const d = this._dragged;
    this._dragged = false;
    return d;
  }

  _forward(out) { return out.set(0, 0, -1).applyQuaternion(this.camera.quaternion); }
  _axis(out, x, y, z) { return out.set(x, y, z).applyQuaternion(this.camera.quaternion); }

  _down(e) {
    if (e.button !== 0 && e.button !== 1 && e.button !== 2) return;
    this._drag = { button: e.button, x: e.clientX, y: e.clientY, moved: 0 };
    this._dragged = false;
    this.element.setPointerCapture?.(e.pointerId);
  }

  _move(e) {
    const d = this._drag;
    if (!d) return;
    const dx = e.clientX - d.x, dy = e.clientY - d.y;
    d.x = e.clientX; d.y = e.clientY;
    d.moved += Math.abs(dx) + Math.abs(dy);
    if (d.moved > 4) this._dragged = true;
    this._syncTarget();
    if (d.button === 0) {
      // turning the view where the camera stands: yaw about its up, pitch about its right
      const q = this.camera.quaternion;
      this._q.setFromAxisAngle(this._v.set(0, 1, 0), -dx * this.lookSpeed);
      q.multiply(this._q);
      this._q.setFromAxisAngle(this._v.set(1, 0, 0), -dy * this.lookSpeed);
      q.multiply(this._q).normalize();
      this._placeTarget();
    } else {
      // sliding across the view, as far as the point looked at moves under the mouse
      const h = this.element.clientHeight || 1;
      const scale = 2 * this._dist * Math.tan((this.camera.fov * Math.PI / 180) / 2) / h;
      const right = this._axis(new Vector3(), 1, 0, 0), up = this._axis(new Vector3(), 0, 1, 0);
      const shift = right.multiplyScalar(-dx * scale).add(up.multiplyScalar(dy * scale));
      this.camera.position.add(shift);
      this.target.add(shift);
      this._lastTarget.copy(this.target);
    }
  }

  _up(e) {
    if (this._drag && this._drag.button === e.button) this._drag = null;
  }

  _wheel(e) {
    e.preventDefault();
    this._syncTarget();
    const k = e.deltaY > 0 ? 1 / this.zoomStep : this.zoomStep;
    this._dist = Math.min(this.maxDistance, Math.max(this.minDistance, this._dist * k));
    this.camera.position.copy(this.target).sub(this._forward(this._v).multiplyScalar(this._dist));
    this._lastTarget.copy(this.target);
  }

  _key(e, down) {
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    const el = e.target;
    if (down && el?.matches && el.matches('textarea, select, input:not([type=checkbox]):not([type=radio]):not([type=button]):not([type=range])')) return;
    if (e.code !== 'KeyQ' && e.code !== 'KeyE') return;
    if (down) this._keys.add(e.code); else this._keys.delete(e.code);
  }

  /// The target moved by the rest of the page: the camera turns to face it (its roll kept) at its distance.
  _syncTarget() {
    if (this.target.equals(this._lastTarget)) return;
    const to = this._v.copy(this.target).sub(this.camera.position);
    const d = to.length();
    if (d > 1e-6) {
      this._dist = d;
      const up = this._axis(new Vector3(), 0, 1, 0);
      this.camera.up.copy(up);
      this.camera.lookAt(this.target);
    }
    this._lastTarget.copy(this.target);
  }

  _placeTarget() {
    this.target.copy(this.camera.position).add(this._forward(this._v).multiplyScalar(this._dist));
    this._lastTarget.copy(this.target);
  }

  /// Once a frame: follow a target the page moved, roll while Q or E is held.
  update(dt = 16) {
    this._syncTarget();
    const roll = (this._keys.has('KeyE') ? 1 : 0) - (this._keys.has('KeyQ') ? 1 : 0);
    if (roll) {
      this._q.setFromAxisAngle(this._v.set(0, 0, 1), -roll * this.rollSpeed * Math.min(dt, 100) / 1000);
      this.camera.quaternion.multiply(this._q).normalize();
    }
    this.camera.up.copy(this._axis(new Vector3(), 0, 1, 0));
  }
}
