// Runs a flattened payoff program over one batch, column by column: the twin of PayoffEvaluator.cs.
// Each column carries its tangents, so pathwise greeks need no code specific to a product.
#pragma once

#include "common.hpp"

namespace pc {

/// Opcodes written by PayoffBytecode.cs. An event is a header, a postfix expression and END.
enum Op : int32_t {
    OP_END = 0,
    OP_CONST = 1,     // constant index
    OP_SPOT = 2,      // asset
    OP_SLOT = 3,      // slot
    OP_ADD = 4,
    OP_SUBTRACT = 5,
    OP_MULTIPLY = 6,
    OP_MAX = 7,
    OP_MIN = 8,
    OP_STEP = 9,      // constant index of the smoothing scale
    EV_STORE = 20,    // date, slot
    EV_PAY = 21,      // date
    EV_TERMINATE = 22 // date
};

/// One event of the program, decoded once.
struct Event {
    int32_t kind;
    int32_t date;
    int32_t slot;
    int32_t begin;  // first opcode of the expression
};

/// Checks the program and lists its events; events are in date order.
inline bool decode(const pc_program& program, std::vector<Event>& events) {
    const int32_t* code = program.code;
    int i = 0;
    while (i < program.code_length) {
        Event e{code[i], 0, -1, 0};
        if (e.kind != EV_STORE && e.kind != EV_PAY && e.kind != EV_TERMINATE) return false;
        if (i + 1 >= program.code_length) return false;
        e.date = code[i + 1];
        i += 2;
        if (e.kind == EV_STORE) {
            if (i >= program.code_length) return false;
            e.slot = code[i++];
            if (e.slot < 0 || e.slot >= program.slots) return false;
        }
        if (e.date < 0 || e.date >= program.dates) return false;
        if (!events.empty() && e.date < events.back().date) return false;
        e.begin = i;
        int depth = 0;
        for (;; i++) {
            if (i >= program.code_length) return false;
            int32_t op = code[i];
            if (op == OP_END) break;
            switch (op) {
                case OP_CONST:
                case OP_SPOT:
                case OP_SLOT:
                    depth++;
                    i++;
                    break;
                case OP_STEP:
                    if (depth < 1) return false;
                    i++;
                    break;
                case OP_ADD:
                case OP_SUBTRACT:
                case OP_MULTIPLY:
                case OP_MAX:
                case OP_MIN:
                    if (depth < 2) return false;
                    depth--;
                    break;
                default:
                    return false;
            }
            if (i >= program.code_length) return false;
            if (op == OP_SPOT && (code[i] < 0 || code[i] >= program.assets)) return false;
            if (op == OP_SLOT && (code[i] < 0 || code[i] >= program.slots)) return false;
        }
        if (depth != 1) return false;
        i++;
        events.push_back(e);
    }
    return true;
}

class PayoffVm {
public:
    PayoffVm(const pc_program& program, int length, int tangents, double smoothing)
        : program_(program), length_(length), tangents_(tangents), smoothing_(smoothing) {
        alive_ = rent();
        std::fill(value(alive_), value(alive_) + length_, 1.0);
        cash_ = rent();
        for (int a = 0; a < program.assets; a++) spots_.push_back(rent());
        for (int s = 0; s < program.slots; s++) {
            int column = rent();
            std::fill(value(column), value(column) + length_, program.initial_slots[s]);
            slots_.push_back(column);
        }
    }

    double* cash() { return value(cash_); }
    double* cash_tangent(int p) { return tangent(cash_, p); }
    double* spot(int asset) { return value(spots_[asset]); }
    double* spot_tangent(int asset, int p) { return tangent(spots_[asset], p); }

    void run(const Event& e, double discount) {
        int result = evaluate(e.begin);
        switch (e.kind) {
            case EV_STORE:
                release(slots_[e.slot]);
                slots_[e.slot] = result;
                return;
            case EV_PAY: {
                const double* alive = value(alive_);
                const double* amount = value(result);
                for (int p = 0; p < tangents_; p++) {
                    double* d = tangent(cash_, p);
                    const double* da = tangent(result, p);
                    const double* dl = tangent(alive_, p);
                    for (int i = 0; i < length_; i++) d[i] += discount * (alive[i] * da[i] + dl[i] * amount[i]);
                }
                double* cash = value(cash_);
                for (int i = 0; i < length_; i++) cash[i] += discount * alive[i] * amount[i];
                release(result);
                return;
            }
            case EV_TERMINATE: {
                double* alive = value(alive_);
                const double* condition = value(result);
                for (int p = 0; p < tangents_; p++) {
                    double* dl = tangent(alive_, p);
                    const double* dc = tangent(result, p);
                    for (int i = 0; i < length_; i++) dl[i] = dl[i] * (1 - condition[i]) - alive[i] * dc[i];
                }
                for (int i = 0; i < length_; i++) alive[i] *= 1 - condition[i];
                release(result);
                return;
            }
        }
    }

private:
    // A column is a block of (1 + tangents) x length doubles: the value, then each tangent.
    double* value(int column) { return blocks_[column].data(); }
    double* tangent(int column, int p) { return blocks_[column].data() + static_cast<size_t>(1 + p) * length_; }

    int rent() {
        if (!free_.empty()) {
            int column = free_.back();
            free_.pop_back();
            return column;
        }
        blocks_.emplace_back(static_cast<size_t>(1 + tangents_) * length_, 0.0);
        return static_cast<int>(blocks_.size()) - 1;
    }

    void release(int column) { free_.push_back(column); }

    int copy(int source) {
        int column = rent();
        std::memcpy(value(column), value(source), sizeof(double) * length_);
        for (int p = 0; p < tangents_; p++) std::memcpy(tangent(column, p), tangent(source, p), sizeof(double) * length_);
        return column;
    }

    int filled(double constant) {
        int column = rent();
        std::fill(value(column), value(column) + length_, constant);
        for (int p = 0; p < tangents_; p++) std::fill(tangent(column, p), tangent(column, p) + length_, 0.0);
        return column;
    }

    int evaluate(int begin) {
        const int32_t* code = program_.code;
        stack_.clear();
        for (int i = begin; code[i] != OP_END; i++) {
            switch (code[i]) {
                case OP_CONST: stack_.push_back(filled(program_.constants[code[++i]])); break;
                case OP_SPOT: stack_.push_back(copy(spots_[code[++i]])); break;
                case OP_SLOT: stack_.push_back(copy(slots_[code[++i]])); break;
                case OP_STEP: step(stack_.back(), program_.constants[code[++i]]); break;
                default: {
                    int right = stack_.back();
                    stack_.pop_back();
                    apply(code[i], stack_.back(), right);
                    release(right);
                    break;
                }
            }
        }
        return stack_.back();
    }

    void apply(int32_t op, int left, int right) {
        double* a = value(left);
        const double* b = value(right);
        for (int p = 0; p < tangents_; p++) {
            double* da = tangent(left, p);
            const double* db = tangent(right, p);
            switch (op) {
                case OP_ADD: for (int i = 0; i < length_; i++) da[i] = da[i] + db[i]; break;
                case OP_SUBTRACT: for (int i = 0; i < length_; i++) da[i] = da[i] - db[i]; break;
                case OP_MULTIPLY: for (int i = 0; i < length_; i++) da[i] = da[i] * b[i] + a[i] * db[i]; break;
                case OP_MAX: for (int i = 0; i < length_; i++) if (b[i] > a[i]) da[i] = db[i]; break;
                case OP_MIN: for (int i = 0; i < length_; i++) if (b[i] < a[i]) da[i] = db[i]; break;
            }
        }
        switch (op) {
            case OP_ADD: for (int i = 0; i < length_; i++) a[i] = a[i] + b[i]; break;
            case OP_SUBTRACT: for (int i = 0; i < length_; i++) a[i] = a[i] - b[i]; break;
            case OP_MULTIPLY: for (int i = 0; i < length_; i++) a[i] = a[i] * b[i]; break;
            case OP_MAX: for (int i = 0; i < length_; i++) a[i] = b[i] > a[i] ? b[i] : a[i]; break;
            case OP_MIN: for (int i = 0; i < length_; i++) a[i] = b[i] < a[i] ? b[i] : a[i]; break;
        }
    }

    /// The value stays a hard step; only its derivative is smoothed, so the PV is unbiased.
    void step(int argument, double scale) {
        double* x = value(argument);
        double width = smoothing_ * scale;
        for (int p = 0; p < tangents_; p++) {
            double* d = tangent(argument, p);
            for (int i = 0; i < length_; i++) d[i] = width > 0 && std::fabs(x[i]) < width ? d[i] / (2 * width) : 0;
        }
        for (int i = 0; i < length_; i++) x[i] = x[i] >= 0 ? 1 : 0;
    }

    const pc_program& program_;
    int length_;
    int tangents_;
    double smoothing_;
    std::vector<std::vector<double>> blocks_;
    std::vector<int> free_;
    std::vector<int> stack_;
    std::vector<int> slots_;
    std::vector<int> spots_;
    int alive_ = 0;
    int cash_ = 0;
};

}  // namespace pc
