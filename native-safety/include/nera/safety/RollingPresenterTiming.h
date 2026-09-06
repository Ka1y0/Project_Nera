// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <iterator>
#include <vector>

namespace nera::phase6
{
    struct PresenterTimingSnapshot final
    {
        // 1=current one-second rate available; 2=rolling interval statistics
        // available; 4=suspended; 8=capacity limited (never claimed full 30s).
        std::uint32_t flags{};
        float presentFps{}, averagePresentFps{}, onePercentLowFps{}, frameTimeP99Ms{};
        std::uint32_t sampleCount{};
    };

    class RollingPresenterTiming final
    {
    public:
        static constexpr std::size_t MaximumSamples = 16'384;
        void Reset(std::uint64_t now, std::uint64_t frequency, bool suspended = false)
        {
            frames_.clear(); started_ = now; frequency_ = frequency;
            suspended_ = suspended; capacityLimited_ = false;
        }
        void RecordSuccessfulPresent(std::uint64_t qpc)
        {
            if (suspended_ || frequency_ == 0 || qpc < started_) return;
            if (!frames_.empty() && qpc <= frames_.back()) return;
            Prune(qpc);
            if (frames_.size() == MaximumSamples)
            { frames_.pop_front(); capacityLimited_ = true; }
            frames_.push_back(qpc);
        }
        PresenterTimingSnapshot Snapshot(std::uint64_t now)
        {
            PresenterTimingSnapshot result;
            if (suspended_) { result.flags = 4; return result; }
            if (frequency_ == 0 || now < started_) return result;
            Prune(now);
            const auto elapsed = now - started_;
            if (elapsed >= frequency_)
            {
                const auto lower = now - frequency_;
                const auto first = std::upper_bound(frames_.begin(), frames_.end(), lower);
                result.presentFps = static_cast<float>(std::distance(first, frames_.end()));
                result.flags |= 1;
            }
            if (capacityLimited_) result.flags |= 8;
            if (frames_.size() < 2) return result;
            std::vector<double> intervals; intervals.reserve(frames_.size() - 1);
            double sum{};
            for (std::size_t index = 1; index < frames_.size(); ++index)
            {
                const double milliseconds = static_cast<double>(frames_[index] - frames_[index - 1]) *
                    1000.0 / static_cast<double>(frequency_);
                if (!(milliseconds > 0)) continue;
                intervals.push_back(milliseconds); sum += milliseconds;
            }
            if (intervals.empty() || !(sum > 0)) return result;
            result.flags |= 2;
            result.sampleCount = static_cast<std::uint32_t>(intervals.size());
            result.averagePresentFps = static_cast<float>(1000.0 * intervals.size() / sum);
            std::sort(intervals.begin(), intervals.end());
            const auto p99 = static_cast<std::size_t>(std::ceil(intervals.size() * 0.99)) - 1;
            result.frameTimeP99Ms = static_cast<float>(intervals[p99]);
            const auto slowCount = (std::max)(std::size_t{1},
                static_cast<std::size_t>(std::ceil(intervals.size() * 0.01)));
            double slowSum{};
            for (auto index = intervals.size() - slowCount; index < intervals.size(); ++index)
                slowSum += intervals[index];
            result.onePercentLowFps = static_cast<float>(1000.0 * slowCount / slowSum);
            return result;
        }
    private:
        void Prune(std::uint64_t now)
        {
            const auto window = frequency_ * 30;
            const auto lower = now > window ? now - window : 0;
            while (!frames_.empty() && frames_.front() < lower) frames_.pop_front();
        }
        std::deque<std::uint64_t> frames_;
        std::uint64_t started_{}, frequency_{};
        bool suspended_{}, capacityLimited_{};
    };
}
