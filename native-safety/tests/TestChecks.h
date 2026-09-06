#pragma once
#include <iostream>

struct TestChecks final
{
    unsigned total{}, failed{};
    void Require(bool condition, const char* label)
    {
        ++total;
        if (!condition) { ++failed; std::cerr << "FAIL: " << label << '\n'; }
    }
    int Finish() const
    {
        std::cout << (total - failed) << '/' << total << " checks passed\n";
        return failed == 0 ? 0 : 1;
    }
};
