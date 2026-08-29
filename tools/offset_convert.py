"""Turn a controller setting into the rotation the game actually applies to the saber.

Read out of Beat Saber 1.45.0's own VRController.TryGetControllerOffset, because every
plausible guess about this is wrong in a way that still looks reasonable.

With alternative handling on, the game does not use the number typed into the settings
screen. It adds a per-manufacturer legacy offset first -- for a Valve Index, -16.3 degrees
of X -- and only then builds the quaternion. For the left hand it also negates Y and Z.
Sixteen degrees of X is not a detail: X is the axis that decides how Y and Z mix, so a
delta computed as though the setting were the whole story comes out pointing somewhere else.

The platform's root pose left-multiplies the result and therefore cancels out of any
difference between two settings, which is what makes this computable outside the game at
all.
"""

from __future__ import annotations

import math

import numpy as np

#: UnityXRHelper.kValveIndexLegacyRotationOffset, added before the Euler is built.
LEGACY_ROTATION = {
    "valve": (-16.3, 0.0, 0.0),
    "oculus": (-40.0, 0.0, 0.0),
    "none": (0.0, 0.0, 0.0),
}


def quat_mul(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return np.array([
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    ])


def euler_to_quat(x: float, y: float, z: float) -> np.ndarray:
    """Unity's ``Quaternion.Euler``: Z applied first, then X, then Y."""
    hx, hy, hz = math.radians(x) / 2, math.radians(y) / 2, math.radians(z) / 2
    qx = np.array([math.sin(hx), 0.0, 0.0, math.cos(hx)])
    qy = np.array([0.0, math.sin(hy), 0.0, math.cos(hy)])
    qz = np.array([0.0, 0.0, math.sin(hz), math.cos(hz)])
    return quat_mul(quat_mul(qy, qx), qz)


def applied_euler(setting, hand: str, *, controller: str = "valve",
                  alternative_handling: bool = True) -> tuple[float, float, float]:
    """The Euler the game feeds to ``Quaternion.Euler`` for this hand.

    ``VRController.TryGetControllerOffset``, alternative-handling branch::

        vector4 = legacyRotationOffset + customRotationOffset
        if left: vector4 = vector4.MirrorEulerAnglesOnYZPlane()   # (x, -y, -z)
        rotation = rootPose.rotation * Quaternion.Euler(vector4)
    """
    if not alternative_handling:
        # The other branch applies the setting unmixed and mirrors the whole pose instead.
        return tuple(float(v) for v in setting)  # type: ignore[return-value]
    legacy = LEGACY_ROTATION[controller]
    total = [setting[i] + legacy[i] for i in range(3)]
    if hand == "left":
        total = [total[0], -total[1], -total[2]]
    return tuple(float(v) for v in total)  # type: ignore[return-value]


def turn_between(before, after, hand: str, **kw) -> np.ndarray:
    """The rotation, in the saber's own frame, that one setting change produces.

    The platform's root pose left-multiplies both sides and cancels here, which is the only
    reason this can be computed without the game running.
    """
    q0 = euler_to_quat(*applied_euler(before, hand, **kw))
    q1 = euler_to_quat(*applied_euler(after, hand, **kw))
    d = quat_mul(np.array([-q0[0], -q0[1], -q0[2], q0[3]]), q1)
    d = d / np.linalg.norm(d)
    if d[3] < 0:
        d = -d
    angle = 2 * math.acos(float(np.clip(d[3], -1.0, 1.0)))
    return np.zeros(3) if angle < 1e-9 else d[:3] / np.linalg.norm(d[:3]) * angle
