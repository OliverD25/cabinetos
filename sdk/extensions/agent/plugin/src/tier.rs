//! The three tiers: how much the agent may do without the user's word. The
//! plugin is the only thing that issues commands, so the tier is decided
//! here and nowhere else.

use crate::cmdline::Access;

/// How much the agent does by itself.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Tier {
    /// 1, Advisor: looks and answers; no change is proposed or made.
    Advisor,
    /// 2, Diff and approve (the default): every change is shown as a
    /// preview; only the user applies it.
    Diff,
    /// 3, Autonomous: the preview is applied at once. Every job is still
    /// in the undo journal. Never the default.
    Autonomous,
}

/// What the tier does with a command.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Decision {
    /// Run it now.
    Run,
    /// Add it to the preview the user will approve.
    Propose,
    /// Add it to a preview and apply the preview at once.
    Apply,
    /// Do not run it, and say so.
    Refuse,
}

impl Tier {
    /// The tier the settings and `agent.tier` use: 1, 2 or 3.
    #[must_use]
    pub fn from_number(number: u64) -> Option<Self> {
        match number {
            1 => Some(Self::Advisor),
            2 => Some(Self::Diff),
            3 => Some(Self::Autonomous),
            _ => None,
        }
    }

    /// 1, 2 or 3.
    #[must_use]
    pub fn number(self) -> u8 {
        match self {
            Self::Advisor => 1,
            Self::Diff => 2,
            Self::Autonomous => 3,
        }
    }

    /// The name the chat page and the audit log show.
    #[must_use]
    pub fn label(self) -> &'static str {
        match self {
            Self::Advisor => "Advisor",
            Self::Diff => "Diff and approve",
            Self::Autonomous => "Autonomous",
        }
    }

    /// What the model is told about it.
    #[must_use]
    pub fn about(self) -> &'static str {
        match self {
            Self::Advisor => {
                "Advisor: you may look (ls, describe, search, state) and advise. Nothing you propose to change is run or shown as a preview: the user reads your commands and does them if they want."
            }
            Self::Diff => {
                "Diff and approve: the commands that change files are collected into a preview that the user sees before anything changes; only the user applies it."
            }
            Self::Autonomous => {
                "Autonomous: the commands that change files are applied at once, and the user can undo them. Be careful and exact."
            }
        }
    }

    /// The decision for one command.
    #[must_use]
    pub fn decide(self, access: Access) -> Decision {
        match (access, self) {
            (Access::Read, _) => Decision::Run,
            (Access::Write, Self::Advisor) => Decision::Refuse,
            (Access::Write, Self::Diff) => Decision::Propose,
            (Access::Write, Self::Autonomous) => Decision::Apply,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_tier_has_a_number_and_a_name() {
        for number in 1..=3 {
            assert_eq!(Tier::from_number(number).unwrap().number(), number as u8);
        }
        assert_eq!(Tier::from_number(0), None);
        assert_eq!(Tier::from_number(4), None);
        assert_eq!(Tier::Diff.label(), "Diff and approve");
    }

    #[test]
    fn every_tier_lets_reads_run_and_only_tier_three_applies_at_once() {
        for tier in [Tier::Advisor, Tier::Diff, Tier::Autonomous] {
            assert_eq!(tier.decide(Access::Read), Decision::Run);
        }
        assert_eq!(Tier::Advisor.decide(Access::Write), Decision::Refuse);
        assert_eq!(Tier::Diff.decide(Access::Write), Decision::Propose);
        assert_eq!(Tier::Autonomous.decide(Access::Write), Decision::Apply);
    }
}
