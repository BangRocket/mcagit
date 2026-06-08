//! `DiffSink` that captures applyable [`PatchOp`]s (base/value via `mca_nbt::to_json`).

use crate::model::PatchOp;
use mca_diff::DiffSink;
use mca_nbt::{to_json, NbtValue};

#[derive(Default)]
pub struct PatchOpSink {
    pub ops: Vec<PatchOp>,
}

impl DiffSink for PatchOpSink {
    fn added(&mut self, path: &str, value: &NbtValue) {
        self.ops.push(PatchOp {
            path: path.to_string(),
            base: None,
            value: Some(to_json(value)),
        });
    }
    fn removed(&mut self, path: &str, value: &NbtValue) {
        self.ops.push(PatchOp {
            path: path.to_string(),
            base: Some(to_json(value)),
            value: None,
        });
    }
    fn modified(&mut self, path: &str, a: &NbtValue, b: &NbtValue) {
        self.ops.push(PatchOp {
            path: path.to_string(),
            base: Some(to_json(a)),
            value: Some(to_json(b)),
        });
    }
    fn type_changed(&mut self, path: &str, a: &NbtValue, b: &NbtValue) {
        self.modified(path, a, b);
    }
    fn array_changed(&mut self, path: &str, a: &NbtValue, b: &NbtValue) {
        self.modified(path, a, b);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use mca_diff::walk;
    use mca_nbt::{Compound, NbtValue};

    fn comp(pairs: &[(&str, NbtValue)]) -> NbtValue {
        let mut m = Compound::new();
        for (k, v) in pairs {
            m.insert((*k).into(), v.clone());
        }
        NbtValue::Compound(m)
    }

    #[test]
    fn records_base_and_value() {
        let a = comp(&[("hp", NbtValue::Int(20)), ("gone", NbtValue::Byte(1))]);
        let b = comp(&[("hp", NbtValue::Int(18)), ("new", NbtValue::Int(7))]);
        let mut sink = PatchOpSink::default();
        walk(&a, &b, &mut sink);
        // gone: removed (base set, value none); hp: modified; new: added (value set, base none)
        let gone = sink.ops.iter().find(|o| o.path == "gone").unwrap();
        assert!(gone.base.is_some() && gone.value.is_none());
        let hp = sink.ops.iter().find(|o| o.path == "hp").unwrap();
        assert!(hp.base.is_some() && hp.value.is_some());
        let new = sink.ops.iter().find(|o| o.path == "new").unwrap();
        assert!(new.base.is_none() && new.value.is_some());
    }
}
